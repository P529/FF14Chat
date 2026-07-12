using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Ui;

/// <summary>
/// Shows the game's own ItemDetail tooltip for a hovered chat item link.
/// The raw id encodes the variant (+1M HQ, 2M+ event item).
/// </summary>
internal static unsafe class NativeItemTooltip
{
    /// <summary>False when the addon/agent isn't available (caller falls back to the custom card).</summary>
    public static bool Open(uint rawItemId)
    {
        var agent = AgentItemDetail.Instance();
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("ItemDetail").Address;
        var stage = AtkStage.Instance();
        if (agent == null || addon == null || stage == null)
            return false;

        agent->DetailKind = rawItemId >= 2_000_000 ? DetailKind.KeyItem : DetailKind.Item;
        agent->TypeOrId = rawItemId;
        agent->Index = 0;
        agent->Flag1 &= 0xEF;
        agent->ItemId = rawItemId;

        // Two flags ClientStructs hasn't named yet; they gate the agent's
        // "hover is valid" state. Offsets are patch-fragile — if tooltips
        // break after a game patch, look here first.
        *(byte*)((nint)agent + 0x21A) = 1;
        *(byte*)((nint)agent + 0x21E) = 0;

        agent->AddonId = addon->Id;

        // Without a tooltip type set, the addon's show handler early-returns.
        stage->TooltipManager.TooltipType |= 2;
        addon->Show(false, 15);
        return true;
    }

    /// <summary>
    /// Places the tooltip beside the chat window. Native addons always
    /// render underneath Dalamud's ImGui layer, so any overlap with our
    /// window would hide the tooltip behind it — it must sit outside.
    /// Called every hovered frame; the addon size settles after frame one.
    /// </summary>
    public static void Reposition(System.Numerics.Vector2 windowPos, System.Numerics.Vector2 windowSize)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("ItemDetail").Address;
        if (addon == null || !addon->IsVisible)
            return;

        // Scaled size includes the root UI scale (WindowNode alone doesn't,
        // which under-measured at non-100% UI scaling). Zero means layout
        // hasn't run yet — skip, next frame has real numbers.
        var width = (float)addon->GetScaledWidth(true);
        var height = (float)addon->GetScaledHeight(true);
        if (width <= 0 || height <= 0)
            return;

        var mouse = ImGui.GetMousePos();
        var viewport = ImGui.GetMainViewport();
        var screenRight = viewport.Pos.X + viewport.Size.X;
        var screenBottom = viewport.Pos.Y + viewport.Size.Y;

        // The window edge with more room gets the tooltip.
        var roomRight = screenRight - (windowPos.X + windowSize.X);
        var roomLeft = windowPos.X - viewport.Pos.X;
        var x = roomRight >= roomLeft
            ? windowPos.X + windowSize.X + 4f
            : windowPos.X - width - 4f;

        var y = mouse.Y - height / 2f;

        // Never off screen, whatever the sizes did above.
        x = System.Math.Clamp(x, viewport.Pos.X, System.Math.Max(viewport.Pos.X, screenRight - width));
        y = System.Math.Clamp(y, viewport.Pos.Y, System.Math.Max(viewport.Pos.Y, screenBottom - height));

        addon->SetPosition((short)x, (short)y);
    }

    public static void Close()
    {
        // Hide the addon before poking the agent so no close sound plays.
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("ItemDetail").Address;
        if (addon != null)
            addon->Hide(true, false, 0);

        var agent = AgentItemDetail.Instance();
        if (agent != null)
        {
            var eventData = stackalloc AtkValue[1];
            var values = stackalloc AtkValue[1];
            values->Type = AtkValueType.Int;
            values->Int = -1;
            agent->ReceiveEvent(eventData, values, 1, 1);
        }
    }
}
