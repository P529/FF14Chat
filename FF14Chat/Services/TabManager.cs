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

    /// <summary>"Name@World" for tell tabs; null for fixed tabs.</summary>
    public string? TellPartner { get; init; }

    public bool IsTell => TellPartner != null;

    public List<Message> Messages { get; } = [];
    public long Revision { get; private set; }
    public int Unread { get; set; }

    // Renderer state, only touched on the draw thread.
    public long RenderedRevision { get; set; } = -1;

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

/// <summary>Routes captured messages into fixed and per-tell-partner tabs.</summary>
public sealed class TabManager
{
    private readonly object gate = new();
    private readonly List<TabState> tabs = [];
    private readonly Configuration configuration;
    private readonly MessageStore store;

    public TabManager(Configuration configuration, MessageStore store)
    {
        this.configuration = configuration;
        this.store = store;

        foreach (var tabConfig in configuration.Tabs)
        {
            tabs.Add(new TabState
            {
                Id = "tab:" + tabConfig.Name,
                Title = tabConfig.Name,
                Channels = [.. tabConfig.Channels],
                CatchAll = tabConfig.CatchAll,
                TrackUnread = tabConfig.NotifyUnread,
            });
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
            var sorted = tabs
                .OrderBy(t =>
                {
                    var index = orderedIds.IndexOf(t.Id);
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();

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
        // Battle log entries pack source/target flags into the high bits;
        // the low 7 bits are the base kind, which is what filters care about.
        var masked = (XivChatType)((ushort)message.Type & 0x7F);

        var reopenedClosedTab = false;
        lock (gate)
        {
            var anyFixedMatch = false;
            foreach (var tab in tabs)
            {
                var matches = tab.IsTell
                    ? message.TellPartner == tab.TellPartner
                    : tab.Channels!.Contains(message.Type) || tab.Channels.Contains(masked);
                if (matches)
                {
                    tab.Add(message);
                    if (!tab.IsTell)
                        anyFixedMatch = true;
                }
            }

            // Unclassified non-combat messages (join notices, obtain lines,
            // unnamed system kinds) land in the catch-all tab. Combat kinds
            // (masked 41..55) stay out to avoid battle spam.
            if (!anyFixedMatch && (ushort)masked is < 41 or > 55)
            {
                foreach (var tab in tabs)
                {
                    if (tab.CatchAll)
                        tab.Add(message);
                }
            }

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

    public TabState[] Snapshot()
    {
        lock (gate)
        {
            return [.. tabs];
        }
    }

    public Message[] MessagesSnapshot(TabState tab)
    {
        lock (gate)
        {
            return [.. tab.Messages];
        }
    }

    /// <summary>Returns the tell tab for a partner, creating an empty one if needed.</summary>
    public TabState OpenTellTab(string partner)
    {
        lock (gate)
        {
            configuration.ClosedTellTabs.Remove(partner);

            var existing = tabs.FirstOrDefault(t => t.TellPartner == partner);
            if (existing != null)
                return existing;

            var tellTab = CreateTellTab(partner);
            tellTab.Unread = 0;
            tabs.Add(tellTab);
            return tellTab;
        }
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
