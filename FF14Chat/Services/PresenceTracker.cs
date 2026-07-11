using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FF14Chat.Services;

public enum PresenceStatus
{
    /// <summary>No source covers this player (not a friend, not nearby, not in party).</summary>
    Unknown,
    Online,
    Afk,
    Offline,
}

/// <summary>
/// Tracks online/AFK status for tell partners. Sources, most authoritative
/// last-writer: the friend list (the only one that can prove offline), the
/// party list, and nearby players from the object table (live AFK flag).
/// Recent chat traffic is the fallback: a message from someone proves they
/// were online moments ago.
/// </summary>
public sealed class PresenceTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FriendRequestInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(5);

    // OnlineStatus sheet row 17 = "Away from Keyboard".
    private const uint OnlineStatusAfk = 17;

    private readonly TabManager tabs;

    // All access happens on the game's main thread (framework update, chat
    // events, and draw all run there), so no locking is needed.
    private readonly Dictionary<string, PresenceStatus> observed = [];
    private readonly Dictionary<string, DateTime> lastActivity = [];

    // Friend statuses stick between successful friend-list reads: the proxy
    // empties during zone changes and while a request is in flight, and a
    // known friend must never fall back to "unknown".
    private readonly Dictionary<string, PresenceStatus> friendStatuses = [];

    private DateTime nextPoll = DateTime.MinValue;
    private DateTime nextFriendRequest = DateTime.MinValue;

    public PresenceTracker(TabManager tabs)
    {
        this.tabs = tabs;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>
    /// A live message involving this player proves them online: their own
    /// chat lines, and delivered tells in either direction (an outgoing tell
    /// only echoes back when the server accepted it).
    /// </summary>
    public void NoteActivity(string partner)
    {
        lastActivity[partner] = DateTime.Now;
    }

    /// <summary>
    /// Retracts an activity note that turned out to prove nothing (e.g. an
    /// outgoing tell echo followed by a delivery failure).
    /// </summary>
    public void ClearActivity(string partner)
    {
        lastActivity.Remove(partner);
    }

    public PresenceStatus StatusFor(string partner)
    {
        if (observed.TryGetValue(partner, out var status))
            return status;

        if (friendStatuses.TryGetValue(partner, out var friendStatus))
            return friendStatus;

        if (lastActivity.TryGetValue(partner, out var seen) && DateTime.Now - seen < ActivityWindow)
            return PresenceStatus.Online;

        return PresenceStatus.Unknown;
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        var now = DateTime.Now;
        if (now < nextPoll || !Plugin.ClientState.IsLoggedIn)
            return;
        nextPoll = now + PollInterval;

        // Only partners with an open tell tab are tracked; nobody else's
        // status is stored, and no friend-list request goes out without one.
        var partners = tabs.TellPartners();
        observed.Clear();
        PruneClosed(friendStatuses, partners);
        PruneClosed(lastActivity, partners);
        if (partners.Count == 0)
            return;

        CollectFriends(now, partners);
        CollectParty(partners);
        CollectNearby(partners);
    }

    /// <summary>
    /// All friends as "Name@World" from the proxy's current snapshot,
    /// requesting a refresh at most once per interval. The refresh lands
    /// asynchronously, so a first call may return a stale or empty list.
    /// </summary>
    public unsafe List<string> FriendNames()
    {
        var result = new List<string>();
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return result;

        var proxy = infoModule->GetInfoProxyById(InfoProxyId.FriendList);
        if (proxy == null)
            return result;

        var now = DateTime.Now;
        if (now >= nextFriendRequest)
        {
            nextFriendRequest = now + FriendRequestInterval;
            proxy->RequestData();
        }

        var friendList = (InfoProxyCommonList*)proxy;
        foreach (ref readonly var friend in friendList->CharDataSpan)
        {
            var name = friend.NameString;
            if (name.Length > 0)
                result.Add(WithWorld(name, friend.HomeWorld));
        }

        return result;
    }

    private unsafe void CollectFriends(DateTime now, HashSet<string> partners)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return;

        var proxy = infoModule->GetInfoProxyById(InfoProxyId.FriendList);
        if (proxy == null)
            return;

        // The proxy only fills on request (the same packet the social window
        // sends); refresh periodically and read whatever it currently holds.
        if (now >= nextFriendRequest)
        {
            nextFriendRequest = now + FriendRequestInterval;
            proxy->RequestData();
        }

        var friendList = (InfoProxyCommonList*)proxy;
        foreach (ref readonly var friend in friendList->CharDataSpan)
        {
            var name = friend.NameString;
            if (name.Length == 0)
                continue;

            var key = WithWorld(name, friend.HomeWorld);
            if (!partners.Contains(key))
                continue;

            var status = friend.State == InfoProxyCommonList.CharacterData.OnlineStatus.Offline
                ? PresenceStatus.Offline
                : friend.State.HasFlag(InfoProxyCommonList.CharacterData.OnlineStatus.AwayFromKeyboard)
                    ? PresenceStatus.Afk
                    : PresenceStatus.Online;
            observed[key] = status;
            friendStatuses[key] = status;
        }
    }

    private void CollectParty(HashSet<string> partners)
    {
        foreach (var member in Plugin.PartyList)
        {
            var name = member.Name.TextValue;
            if (name.Length == 0)
                continue;

            var key = WithWorld(name, member.World.RowId);
            if (!partners.Contains(key))
                continue;

            // No AFK information here; don't downgrade a friend-list AFK.
            observed.TryAdd(key, PresenceStatus.Online);
        }
    }

    private void CollectNearby(HashSet<string> partners)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            var name = player.Name.TextValue;
            if (name.Length == 0)
                continue;

            var key = WithWorld(name, player.HomeWorld.RowId);
            if (!partners.Contains(key))
                continue;

            // Live data, fresher than a stale friend-list snapshot: overwrite.
            observed[key] =
                player.OnlineStatus.RowId == OnlineStatusAfk ? PresenceStatus.Afk : PresenceStatus.Online;
        }
    }

    /// <summary>Drops remembered state for partners whose tell tab was closed.</summary>
    private static void PruneClosed<T>(Dictionary<string, T> map, HashSet<string> partners)
    {
        List<string>? stale = null;
        foreach (var key in map.Keys)
        {
            if (!partners.Contains(key))
                (stale ??= []).Add(key);
        }

        if (stale == null)
            return;

        foreach (var key in stale)
            map.Remove(key);
    }

    internal static string WithWorld(string name, uint worldId)
    {
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().TryGetRow(worldId, out var world)
            ? $"{name}@{world.Name.ExtractText()}"
            : name;
    }
}
