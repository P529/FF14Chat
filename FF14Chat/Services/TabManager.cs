using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using FF14Chat.Model;

namespace FF14Chat.Services;

public sealed class TabState
{
    private const int MaxMessages = 10_000;

    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Channel filter for fixed tabs; null for tell tabs.</summary>
    public HashSet<XivChatType>? Channels { get; init; }

    /// <summary>Also receives non-combat messages no other tab matched.</summary>
    public bool CatchAll { get; init; }

    /// <summary>Whether arriving messages increment the unread badge.</summary>
    public bool TrackUnread { get; init; }

    /// <summary>Chat command plain text is sent through (e.g. "/p"); null = active channel.</summary>
    public string? SendCommand { get; init; }

    /// <summary>"Name@World" for tell tabs; null for fixed tabs.</summary>
    public string? TellPartner { get; init; }

    public bool IsTell => TellPartner != null;

    public List<Message> Messages { get; } = [];
    public long Revision { get; private set; }
    public int Unread { get; set; }

    // Renderer state, only touched on the draw thread.
    public long RenderedRevision { get; set; } = -1;

    // Snapshot cache for MessagesSnapshot; guarded by the TabManager gate.
    internal Message[]? SnapshotCache;
    internal long SnapshotRevision = -1;

    private string? labelPlain;
    private string? labelWithDot;

    /// <summary>
    /// Stable ImGui tab label ("Title  ###Id"): trailing spaces reserve room
    /// for the unread badge overlay, leading spaces for the presence dot.
    /// Cached — Title and Id are immutable and this draws every frame.
    /// </summary>
    public string Label(bool presenceDot) => presenceDot
        ? labelWithDot ??= $"  {Title}  ###{Id}"
        : labelPlain ??= $"{Title}  ###{Id}";

    internal void Add(Message message)
    {
        Messages.Add(message);
        if (Messages.Count > MaxMessages)
            Messages.RemoveRange(0, Messages.Count - MaxMessages);
        Revision++;
        if (TrackUnread)
            Unread++;
    }
}

/// <summary>
/// Routes captured messages into fixed and per-tell-partner tabs.
/// Threading: today every caller (chat events, framework update, ImGui draw)
/// runs on the game's main thread, so the gate is defensive rather than
/// load-bearing — it future-proofs routing ever moving off-thread. That is
/// also why TabState.Unread/Revision may be read outside the lock.
/// </summary>
public sealed class TabManager
{
    private readonly object gate = new();
    private readonly List<TabState> tabs = [];
    private readonly List<TabState> fixedScratch = [];
    private readonly Configuration configuration;
    private readonly MessageStore store;

    public TabManager(Configuration configuration, MessageStore store)
    {
        this.configuration = configuration;
        this.store = store;

        tabs.AddRange(BuildFixedTabs());
        ApplySavedOrder();
    }

    private List<TabState> BuildFixedTabs()
    {
        var result = new List<TabState>();
        TabState? combined = null;

        foreach (var tabConfig in configuration.Tabs)
        {
            var isGeneral = tabConfig.Name == "General";
            var isSystem = tabConfig.Name == "System";

            if (configuration.CombineGeneralSystem && (isGeneral || isSystem))
            {
                if (combined == null)
                {
                    // Reuses General's id so it keeps General's saved order slot.
                    combined = new TabState
                    {
                        Id = "tab:General",
                        Title = "All",
                        Channels = [.. tabConfig.Channels],
                        CatchAll = true,
                    };
                    result.Add(combined);
                }
                else
                {
                    combined.Channels!.UnionWith(tabConfig.Channels);
                }

                continue;
            }

            result.Add(new TabState
            {
                Id = "tab:" + tabConfig.Name,
                Title = tabConfig.Name,
                Channels = [.. tabConfig.Channels],
                CatchAll = tabConfig.CatchAll,
                TrackUnread = tabConfig.NotifyUnread,
                SendCommand = tabConfig.SendCommand,
            });
        }

        return result;
    }

    /// <summary>
    /// Rebuilds the fixed tabs from config (e.g. after toggling the combined
    /// All tab), backfilling them from the message store. Tell tabs survive.
    /// </summary>
    public void RebuildFixedTabs()
    {
        lock (gate)
        {
            tabs.RemoveAll(t => !t.IsTell);
            var fixedTabs = BuildFixedTabs();

            foreach (var message in store.Snapshot())
                AddToFixedTabs(fixedTabs, message);

            foreach (var tab in fixedTabs)
                tab.Unread = 0;

            tabs.InsertRange(0, fixedTabs);
        }

        ApplySavedOrder();
    }

    /// <summary>Reorders tabs to match the saved order; unknown ids keep their position at the end.</summary>
    public void ApplySavedOrder()
    {
        lock (gate)
        {
            var order = configuration.TabOrder;
            if (order.Count == 0)
                return;

            var sorted = tabs
                .OrderBy(t =>
                {
                    var index = order.IndexOf(t.Id);
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();
            tabs.Clear();
            tabs.AddRange(sorted);
        }
    }

    /// <summary>
    /// Aligns the tab list with the display order reported by ImGui and
    /// persists it when it changed. Ids not in the list keep their position.
    /// </summary>
    public void SetOrder(List<string> orderedIds)
    {
        if (orderedIds.Count == 0)
            return;

        var changed = false;
        lock (gate)
        {
            // This syncs every frame and the order almost never changes;
            // detect "already in this relative order" without allocating.
            var lastIndex = -1;
            var inOrder = true;
            foreach (var tab in tabs)
            {
                var index = orderedIds.IndexOf(tab.Id);
                if (index < 0)
                    continue;
                if (index < lastIndex)
                {
                    inOrder = false;
                    break;
                }

                lastIndex = index;
            }

            if (inOrder)
                return;

            // Tabs absent from the display order (hidden, e.g. the FC tab
            // without a free company) keep their current slot; only the
            // listed ones reorder among themselves. Sorting absentees to the
            // end here would persist that position and lose theirs.
            var listed = tabs
                .Where(t => orderedIds.Contains(t.Id))
                .OrderBy(t => orderedIds.IndexOf(t.Id))
                .ToList();

            var sorted = new List<TabState>(tabs.Count);
            var next = 0;
            foreach (var tab in tabs)
                sorted.Add(orderedIds.Contains(tab.Id) ? listed[next++] : tab);

            if (!sorted.SequenceEqual(tabs))
            {
                tabs.Clear();
                tabs.AddRange(sorted);
                configuration.TabOrder = [.. tabs.Select(t => t.Id)];
                changed = true;
            }
        }

        if (changed)
            configuration.Save();
    }

    /// <summary>
    /// Routes a message into matching tabs. Hydration passes live=false so
    /// closed tell tabs stay closed until the partner actually chats again.
    /// </summary>
    public void Route(Message message, bool live = true)
    {
        var reopenedClosedTab = false;
        lock (gate)
        {
            fixedScratch.Clear();
            foreach (var tab in tabs)
            {
                if (tab.IsTell)
                {
                    if (message.TellPartner == tab.TellPartner)
                        tab.Add(message);
                }
                else
                {
                    fixedScratch.Add(tab);
                }
            }

            AddToFixedTabs(fixedScratch, message);

            if (message.TellPartner is { } partner && !tabs.Any(t => t.TellPartner == partner))
            {
                if (!live && configuration.ClosedTellTabs.Contains(partner))
                    return;

                if (live)
                    reopenedClosedTab = configuration.ClosedTellTabs.Remove(partner);

                // The triggering message is already in the store, so the
                // backfill includes it.
                var tellTab = CreateTellTab(partner);
                tellTab.Unread = live ? 1 : 0;
                tabs.Add(tellTab);
            }
        }

        if (reopenedClosedTab)
            configuration.Save();
    }

    /// <summary>Partners of the currently open tell tabs.</summary>
    public HashSet<string> TellPartners()
    {
        lock (gate)
        {
            return [.. tabs.Where(t => t.IsTell).Select(t => t.TellPartner!)];
        }
    }

    public TabState[] Snapshot()
    {
        lock (gate)
        {
            return [.. tabs];
        }
    }

    /// <summary>
    /// The tab's messages for rendering; the copy is cached and only rebuilt
    /// when the tab's revision moved (this is called every frame).
    /// </summary>
    public Message[] MessagesSnapshot(TabState tab)
    {
        lock (gate)
        {
            if (tab.SnapshotCache == null || tab.SnapshotRevision != tab.Revision)
            {
                tab.SnapshotCache = [.. tab.Messages];
                tab.SnapshotRevision = tab.Revision;
            }

            return tab.SnapshotCache;
        }
    }

    /// <summary>
    /// Adds a message to every matching fixed tab. Unclassified non-combat
    /// messages (join notices, obtain lines, unnamed system kinds) land in
    /// the catch-all tab; combat kinds stay out (also guards old persisted
    /// rows during hydration). Tells always live in their tell tab, so a
    /// fixed tab that has tell channels unticked must not get them back via
    /// catch-all. Caller holds the gate.
    /// </summary>
    private static void AddToFixedTabs(List<TabState> fixedTabs, Message message)
    {
        var masked = ChatTypes.Mask(message.Type);
        var anyMatch = false;
        foreach (var tab in fixedTabs)
        {
            if (tab.Channels!.Contains(message.Type) || tab.Channels.Contains(masked))
            {
                tab.Add(message);
                anyMatch = true;
            }
        }

        if (anyMatch || message.TellPartner != null || ChatTypes.IsBattleSpam(message.Type))
            return;

        foreach (var tab in fixedTabs)
        {
            if (tab.CatchAll)
                tab.Add(message);
        }
    }

    /// <summary>Returns the tell tab for a partner, creating an empty one if needed.</summary>
    public TabState OpenTellTab(string partner)
    {
        bool reopenedClosedTab;
        TabState tellTab;
        lock (gate)
        {
            // Persist the removal, or a restart resurrects the closed state
            // and the hydration pass skips this partner's tab.
            reopenedClosedTab = configuration.ClosedTellTabs.Remove(partner);

            var existing = tabs.FirstOrDefault(t => t.TellPartner == partner);
            if (existing != null)
            {
                tellTab = existing;
            }
            else
            {
                tellTab = CreateTellTab(partner);
                tellTab.Unread = 0;
                tabs.Add(tellTab);
            }
        }

        if (reopenedClosedTab)
            configuration.Save();
        return tellTab;
    }

    /// <summary>
    /// Builds a tell tab backfilled with the partner's conversation from the
    /// message store, so closing and reopening a tab never loses history.
    /// </summary>
    private TabState CreateTellTab(string partner)
    {
        var tellTab = new TabState
        {
            Id = "tell:" + partner,
            Title = partner.Split('@')[0],
            TellPartner = partner,
            TrackUnread = true,
        };

        foreach (var message in store.Snapshot())
        {
            if (message.TellPartner == partner)
                tellTab.Add(message);
        }

        return tellTab;
    }

    public void MarkRead(TabState tab)
    {
        lock (gate)
        {
            tab.Unread = 0;
        }
    }

    public void Close(TabState tab)
    {
        lock (gate)
        {
            tabs.Remove(tab);

            // Closed tell tabs must not resurrect from history on next load.
            if (tab.TellPartner is { } partner && !configuration.ClosedTellTabs.Contains(partner))
                configuration.ClosedTellTabs.Add(partner);
        }

        configuration.Save();
    }
}
