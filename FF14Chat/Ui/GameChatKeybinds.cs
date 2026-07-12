using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FF14Chat.Ui;

/// <summary>
/// Honors the game's own chat keybinds (Alt+R reply, Alt+P party, …).
/// While the game has keyboard focus it handles these itself and our
/// activation hook routes the result here — but once our ImGui window holds
/// the keyboard the game never sees the keys, so they are re-implemented.
/// Handled keys are cleared from KeyState, which also stops the game from
/// double-handling in the frame where both could fire.
/// </summary>
internal sealed class GameChatKeybinds
{
    private enum ActionKind
    {
        Focus,
        Slash,
        Reply,
        Channel,
    }

    private readonly record struct BindAction(ActionKind Kind, int Channel = 0, uint Linkshell = 0);

    private readonly record struct KeyCombo(VirtualKey Key, int Modifiers);

    // RaptureShellModule channel numbers (see ActiveGameChannel): 1 say,
    // 2 party, 3 alliance, 4 yell, 5 shout, 6 fc, 7 pvp team, 8 novice
    // network, 9+n CWLS(n), 19+n LS(n).
    private static readonly (string Name, BindAction Action)[] Commands = BuildCommands();

    private static (string, BindAction)[] BuildCommands()
    {
        var list = new List<(string, BindAction)>
        {
            ("CMD_CHAT", new BindAction(ActionKind.Focus)),
            ("CMD_COMMAND", new BindAction(ActionKind.Slash)),
            ("CMD_REPLY", new BindAction(ActionKind.Reply)),
            ("CMD_REPLY_REV", new BindAction(ActionKind.Reply)),
            ("CMD_SAY", new BindAction(ActionKind.Channel, 1)),
            ("CMD_YELL", new BindAction(ActionKind.Channel, 4)),
            ("CMD_SHOUT", new BindAction(ActionKind.Channel, 5)),
            ("CMD_PARTY", new BindAction(ActionKind.Channel, 2)),
            ("CMD_ALLIANCE", new BindAction(ActionKind.Channel, 3)),
            ("CMD_FREECOM", new BindAction(ActionKind.Channel, 6)),
            ("PVPTEAM_CHAT", new BindAction(ActionKind.Channel, 7)),
            ("CMD_BEGINNER", new BindAction(ActionKind.Channel, 8)),
        };

        for (var i = 0; i < 8; i++)
        {
            list.Add(($"CMD_CWLINKSHELL_{i + 1}", new BindAction(ActionKind.Channel, 9 + i, (uint)i)));
            list.Add(($"CMD_LINKSHELL_{i + 1}", new BindAction(ActionKind.Channel, 19 + i, (uint)i)));
        }

        // Every chat bind except CMD_CHAT/CMD_COMMAND has a separately
        // configured "always enabled" twin.
        var baseCount = list.Count;
        for (var i = 2; i < baseCount; i++)
            list.Add((list[i].Item1 + "_ALWAYS", list[i].Item2));

        return [.. list];
    }

    private readonly Dictionary<string, (KeyCombo A, KeyCombo B)> binds = [];

    // 0, not long.MinValue: TickCount64 - MinValue overflows negative and
    // the refresh would never fire.
    private long lastRefresh;

    /// <param name="fromImGui">
    /// False: framework-update poll via game KeyState (runs before the game's
    /// input dispatch). True: draw-time poll via ImGui key events — needed
    /// while our input field is focused, because Dalamud suppresses game key
    /// state whenever ImGui captures the keyboard.
    /// </param>
    public unsafe void Poll(MainWindow window, bool fromImGui)
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        if (Environment.TickCount64 - lastRefresh > 5000)
        {
            RefreshBinds();
            lastRefresh = Environment.TickCount64;
        }

        // A native text field has focus (vanilla chat input, renames, …).
        var atkModule = RaptureAtkModule.Instance();
        if (atkModule == null || atkModule->AtkModule.IsTextInputActive())
            return;

        var modifiers = fromImGui
            ? (ImGui.GetIO().KeyShift ? 1 : 0)
              | (ImGui.GetIO().KeyCtrl ? 2 : 0)
              | (ImGui.GetIO().KeyAlt ? 4 : 0)
            : (Plugin.KeyState[VirtualKey.SHIFT] ? 1 : 0)
              | (Plugin.KeyState[VirtualKey.CONTROL] ? 2 : 0)
              | (Plugin.KeyState[VirtualKey.MENU] ? 4 : 0);

        // Of all matching combos, the one with the most modifiers wins
        // (Ctrl+Alt+X must beat a plain Alt+X bound elsewhere).
        (VirtualKey Key, BindAction Action, int Bits)? best = null;
        foreach (var (name, action) in Commands)
        {
            if (!binds.TryGetValue(name, out var combo))
                continue;

            Consider(combo.A, action);
            Consider(combo.B, action);

            void Consider(KeyCombo candidate, BindAction bindAction)
            {
                if (candidate.Key == VirtualKey.NO_KEY || candidate.Modifiers != modifiers)
                    return;

                // While typing, only Ctrl/Alt combos may fire; bare keys and
                // Shift+key belong to the input field.
                if (fromImGui && (candidate.Modifiers & ~1) == 0)
                    return;

                if (!KeyPressed(candidate.Key))
                    return;

                var bits = BitOperations.PopCount((uint)candidate.Modifiers);
                if (best == null || bits >= best.Value.Bits)
                    best = (candidate.Key, bindAction, bits);
            }

            bool KeyPressed(VirtualKey key)
            {
                if (fromImGui)
                {
                    var imguiKey = Dalamud.Interface.Utility.ImGuiHelpers.VirtualKeyToImGuiKey(key);
                    return imguiKey != ImGuiKey.None && ImGui.IsKeyPressed(imguiKey, false);
                }

                return Plugin.KeyState.IsVirtualKeyValid(key) && Plugin.KeyState[key];
            }
        }

        if (best is not { } hit)
            return;

        Plugin.Log.Debug(
            "Game keybind hit: source={Source} key={Key} mods={Mods} action={Action} channel={Channel}",
            fromImGui ? "imgui" : "game", hit.Key, modifiers, hit.Action.Kind, hit.Action.Channel);
        if (Plugin.KeyState.IsVirtualKeyValid(hit.Key))
            Plugin.KeyState[hit.Key] = false;
        switch (hit.Action.Kind)
        {
            case ActionKind.Focus:
                window.GameKeybindFocus();
                break;
            case ActionKind.Slash:
                window.GameKeybindSlash();
                break;
            case ActionKind.Reply:
                window.GameKeybindReply();
                break;
            case ActionKind.Channel:
                window.GameKeybindChannel(hit.Action.Channel, hit.Action.Linkshell);
                break;
        }
    }

    private unsafe void RefreshBinds()
    {
        var input = UIInputData.Instance();
        if (input == null)
            return;

        foreach (var (name, _) in Commands)
        {
            var data = new FFXIVClientStructs.FFXIV.Client.System.Input.Keybind();
            var nameString = Utf8String.FromString(name);
            try
            {
                input->GetKeybindByName(nameString, &data);
            }
            finally
            {
                nameString->Dtor(true);
            }

            binds[name] = (
                new KeyCombo((VirtualKey)data.KeySettings[0].Key, (int)data.KeySettings[0].KeyModifier),
                new KeyCombo((VirtualKey)data.KeySettings[1].Key, (int)data.KeySettings[1].KeyModifier));
        }
    }
}
