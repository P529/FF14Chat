using Dalamud.Game.Text;

namespace FF14Chat.Services;

public static class ChatTypes
{
    /// <summary>
    /// Combat log kinds (damage, healing, buffs...). The high bits carry
    /// source/target flags; the low 7 bits are the base kind. No tab shows
    /// these and they vastly outnumber chat, so they're dropped at capture.
    /// </summary>
    public static bool IsBattleSpam(XivChatType type)
    {
        var kind = (ushort)type & 0x7F;
        return kind is >= 41 and <= 55;
    }
}
