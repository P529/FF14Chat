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

    public TabManager(Configuration configuration)
    {
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
    }

    public void Route(Message message)
    {
        // Battle log entries pack source/target flags into the high bits;
        // the low 7 bits are the base kind, which is what filters care about.
        var masked = (XivChatType)((ushort)message.Type & 0x7F);

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
                var tellTab = new TabState
                {
                    Id = "tell:" + partner,
                    Title = partner.Split('@')[0],
                    TellPartner = partner,
                    TrackUnread = true,
                };
                tellTab.Add(message);
                tabs.Add(tellTab);
            }
        }
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
            var existing = tabs.FirstOrDefault(t => t.TellPartner == partner);
            if (existing != null)
                return existing;

            var tellTab = new TabState
            {
                Id = "tell:" + partner,
                Title = partner.Split('@')[0],
                TellPartner = partner,
                TrackUnread = true,
            };
            tabs.Add(tellTab);
            return tellTab;
        }
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
        }
    }
}
