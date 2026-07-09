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
            });
        }
    }

    public void Route(Message message)
    {
        lock (gate)
        {
            foreach (var tab in tabs)
            {
                var matches = tab.IsTell
                    ? message.TellPartner == tab.TellPartner
                    : tab.Channels!.Contains(message.Type);
                if (matches)
                    tab.Add(message);
            }

            if (message.TellPartner is { } partner && !tabs.Any(t => t.TellPartner == partner))
            {
                var tellTab = new TabState
                {
                    Id = "tell:" + partner,
                    Title = partner.Split('@')[0],
                    TellPartner = partner,
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
