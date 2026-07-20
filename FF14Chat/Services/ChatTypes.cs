using Dalamud.Game.Text;

namespace FF14Chat.Services;

public static class ChatTypes
{
    /// <summary>
    /// The high bits of a chat type carry source/target flags; the low
    /// 7 bits are the base kind, which is what filters care about.
    /// </summary>
    public static XivChatType Mask(XivChatType type) => (XivChatType)((ushort)type & 0x7F);

    /// <summary>
    /// Combat log kinds (damage, healing, buffs...). No tab shows these and
    /// they vastly outnumber chat, so they're dropped at capture.
    /// </summary>
    public static bool IsBattleSpam(XivChatType type) =>
        (ushort)Mask(type) is >= 41 and <= 55;
}
