using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace FF14Chat.Services;

/// <summary>Re-fires a chat link another plugin registered, since we render
/// chat ourselves instead of letting the game's log dispatch it.</summary>
public static class ChatLinkActions
{
    public static void Invoke(DalamudLinkPayload link)
    {
        if (!Plugin.ChatGui.RegisteredLinkHandlers.TryGetValue((link.Plugin, link.CommandId), out var handler))
        {
            Plugin.Log.Warning("No handler registered for {Plugin} link {CommandId}", link.Plugin, link.CommandId);
            return;
        }

        // Handlers may run game functions; keep them on the framework thread.
        // The handler belongs to another plugin, so it gets the same treatment
        // as any other foreign call: whatever it throws stops here.
        Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                handler.Invoke(link.CommandId, new SeString(link));
            }
            catch (System.Exception e)
            {
                Plugin.Log.Error(e, "{Plugin} link handler failed", link.Plugin);
            }
        });
    }
}
