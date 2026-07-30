using System;
using Dalamud.Hooking;
using FF14Chat.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Ui;

/// <summary>
/// The native hooks the window needs to behave like the game's own chat:
/// the ChatLog addon's activate event (so the vanilla input never steals
/// focus from ours), the agent's channel-name change, and the social
/// window's Send Tell. These run on the game's terms rather than ImGui's,
/// so they are kept apart from the drawing code.
/// </summary>
public partial class MainWindow
{
    // The ChatLog addon's event handler; event 0x31 with value 0x05/0x0C is
    // the game asking the vanilla chat input to activate and take focus
    // (chat keybind, reply, social-window Send Tell). Signature as used by
    // ChatTwo for the same purpose.
    private const string ChatLogActivateSig =
        "40 53 57 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4D 8B F8";

    private unsafe delegate byte ChatLogActivateDelegate(nint addon, ushort eventId, AtkValue* value);

    private readonly Hook<ChatLogActivateDelegate>? chatLogActivateHook;

    /// <summary>
    /// While we're the active chat, chat-activation requests focus our input
    /// instead of the vanilla one. The tell target (if any) was already set:
    /// ChangeChannelName ran before this event fired.
    /// </summary>
    private unsafe byte ChatLogActivateDetour(nint addon, ushort eventId, AtkValue* value)
    {
        try
        {
            if (eventId == 0x31 && value != null && value->UInt is 0x05 or 0x0C)
            {
                // The third value can carry text the game wants pre-filled
                // into the input — the social window's Send Tell passes the
                // whole "/tell Name@World " command this way instead of
                // switching the chat mode.
                var insertValue = value + 2;
                var insert = ((int)insertValue->Type & 0xF) == (int)AtkValueType.String
                             && insertValue->String.HasValue
                    ? insertValue->String.ToString()
                    : string.Empty;

                var consume = IsOpen && plugin.Configuration.HideVanillaChat && Plugin.ClientState.IsLoggedIn;
                Plugin.Log.Debug(
                    "ChatLog activate: value={Value:X} insert='{Insert}' consumed={Consumed}",
                    value->UInt, insert, consume);
                if (consume)
                {
                    if (insert.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
                    {
                        var partner = insert["/tell ".Length..].Trim();
                        Plugin.Framework.RunOnTick(() => OpenTellTabFor(partner));
                    }
                    else if (insert.Length > 0)
                    {
                        pendingInsert = insert;
                    }

                    // 0x0C is the tell-target activation; only that flavor
                    // may trust the staged Temp* fields (they linger after
                    // we swallow the commit, so a plain activation must not
                    // read them).
                    ScheduleTellTabSync(includeStaged: value->UInt == 0x0C);
                    focusInput = true;
                    return 1;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "ChatLog activate detour failed");
        }

        return chatLogActivateHook!.Original(addon, eventId, value);
    }

    /// <summary>Opens/selects the tell tab for "Name@World" (world appended from the local player if missing).</summary>
    private void OpenTellTabFor(string partner)
    {
        if (partner.Length == 0)
            return;

        if (!partner.Contains('@')
            && Plugin.ObjectTable.LocalPlayer is { } local
            && local.HomeWorld.ValueNullable?.Name.ExtractText() is { Length: > 0 } homeWorld)
        {
            partner = $"{partner}@{homeWorld}";
        }

        Plugin.Log.Debug("Opening tell tab for '{Partner}'", partner);
        var tellTab = tabs.OpenTellTab(partner);
        selectTabId = tellTab.Id;
        focusInput = true;
    }

    private unsafe delegate nint ChangeChannelNameDelegate(AgentChatLog* agent);

    private readonly Hook<ChangeChannelNameDelegate> changeChannelNameHook;

    /// <summary>
    /// Runs whenever the game's input channel label changes (this is the
    /// path the social window / friend list uses, which never goes through
    /// SetContextTellTarget). The agent's tell fields are not final yet at
    /// this point mid-flow — the target is written after the label updates —
    /// so the actual read happens one tick later.
    /// </summary>
    private unsafe nint ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var result = changeChannelNameHook.Original(agent);

        // Same rule as the other detours: nothing may throw into game code.
        try
        {
            ScheduleTellTabSync(includeStaged: false);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "ChangeChannelName sync failed");
        }

        return result;
    }

    private void ScheduleTellTabSync(bool includeStaged)
    {
        Plugin.Framework.RunOnTick(() => SyncTellTabFromAgent(includeStaged));
    }

    /// <summary>
    /// If the game's input channel is a tell, mirror its target into a tell
    /// tab. Runs a tick after a channel change or chat activation, when the
    /// agent's target fields are complete.
    /// </summary>
    private unsafe void SyncTellTabFromAgent(bool includeStaged)
    {
        try
        {
            if (!IsOpen || !plugin.Configuration.HideVanillaChat)
                return;

            var shell = RaptureShellModule.Instance();
            var agent = AgentChatLog.Instance();
            if (shell == null || agent == null)
                return;

            // The social window stages a pending tell in the Temp* fields and
            // fires the chat-activation event; the vanilla handler we swallow
            // is what would commit them to the active fields. Prefer staged.
            var chatType = includeStaged ? shell->TempChatType : 0;
            var name = includeStaged ? shell->TempTellName.ToString() : string.Empty;
            var world = includeStaged ? shell->TempTellWorld.ToString() : string.Empty;
            var worldId = includeStaged ? shell->TempTellWorldId : (ushort)0;

            Plugin.Log.Debug(
                "Tell sync: staged={Staged} temp={TempChatType}:'{TempName}'@'{TempWorld}'/{TempWorldId} committed={ChatType}:'{Name}'@'{World}' agent={Channel}:'{AgentName}'@{AgentWorldId}",
                includeStaged,
                shell->TempChatType, shell->TempTellName.ToString(), shell->TempTellWorld.ToString(), shell->TempTellWorldId,
                shell->ChatType, shell->TellName.ToString(), shell->TellWorld.ToString(),
                (int)agent->CurrentChannel, agent->TellPlayerName.ToString(), agent->TellWorldId);

            // RaptureShellModule chat types 17/18 are the two tell modes.
            if (chatType is not (17 or 18) || name.Length == 0)
            {
                chatType = shell->ChatType;
                name = shell->TellName.ToString();
                world = shell->TellWorld.ToString();
                worldId = shell->TellWorldId;
            }

            if (chatType is not (17 or 18) || name.Length == 0)
                return;

            if (world.Length == 0)
                world = GameData.WorldName(worldId);

            var partner = world.Length > 0 ? $"{name}@{world}" : name;
            var tellTab = tabs.OpenTellTab(partner);
            selectTabId = tellTab.Id;
            focusInput = true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Tell tab sync failed");
        }
    }

    private unsafe delegate bool SetContextTellTargetDelegate(
        RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName,
        ushort worldId, ulong contentId, ulong accountId, ushort reason, bool a8);

    private readonly Hook<SetContextTellTargetDelegate> setContextTellTargetHook;

    /// <summary>
    /// "Send Tell" from the game's context menus lands here. While we're the
    /// active chat, open our tell tab instead of the native chat input.
    /// </summary>
    private unsafe bool SetContextTellTargetDetour(
        RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName,
        ushort worldId, ulong contentId, ulong accountId, ushort reason, bool a8)
    {
        try
        {
            Plugin.Log.Debug(
                "SetContextTellTarget: name='{Name}' world='{World}' worldId={WorldId} reason={Reason} open={Open} hideVanilla={Hide}",
                playerName != null ? playerName->ToString() : "<null>",
                worldName != null ? worldName->ToString() : "<null>",
                worldId, reason, IsOpen, plugin.Configuration.HideVanillaChat);

            if (IsOpen && plugin.Configuration.HideVanillaChat && playerName != null)
            {
                var name = playerName->ToString();
                var world = worldName != null ? worldName->ToString() : string.Empty;
                if (world.Length == 0)
                    world = GameData.WorldName(worldId);

                if (name.Length > 0)
                {
                    var tellTab = tabs.OpenTellTab(world.Length > 0 ? $"{name}@{world}" : name);
                    selectTabId = tellTab.Id;
                    focusInput = true;
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "SetContextTellTarget detour failed");
        }

        return setContextTellTargetHook.Original(module, playerName, worldName, worldId, contentId, accountId, reason, a8);
    }
}
