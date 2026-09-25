using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;

namespace Wino.Mail.Controls.Playground.ViewModels;

/// <summary>
/// Drives every mutation the mail list must survive: single and bulk adds and removals,
/// in-place updates that move rows between groups or threads, and projection option
/// changes. The data is deterministic so a scenario can be replayed.
/// </summary>
public sealed class MailListInteractionsPageViewModel : INotifyPropertyChanged
{
    private static readonly string[] Senders =
    [
        "Avery Stone", "Jordan Blake", "Casey Morgan", "Robin Shah", "Drew Ellis",
        "Quinn Parker", "Morgan Lee", "Riley Chen", "Sam Rivera", "Jamie Park",
    ];

    private static readonly string[] Subjects =
    [
        "Weekly status update", "Invoice clarification", "Launch checklist", "Customer feedback",
        "Planning notes", "Follow-up required", "Design review", "Release timing",
    ];

    private readonly DateTime _seedTime = DateTime.Now;
    private int _sequence;
    private int _removalSequence;
    private MailListProjectionOptions _projectionOptions = new();
    private MailListSortMode _sortMode = MailListSortMode.Date;
    private MailListGroupMode _groupMode = MailListGroupMode.Date;
    private bool _isThreadingEnabled = true;
    private bool _isPinnedFirst = true;

    public MailListInteractionsPageViewModel()
    {
        Items.CollectionChanged += OnItemsCollectionChanged;
        Seed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MailListCollection<MailListLabItem> Items { get; } = [];

    /// <summary>Most recent collection changes, newest first.</summary>
    public ObservableCollection<string> Log { get; } = [];

    public MailListProjectionOptions ProjectionOptions
    {
        get => _projectionOptions;
        private set
        {
            _projectionOptions = value;
            OnPropertyChanged();
        }
    }

    public MailListSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value)
            {
                return;
            }

            _sortMode = value;
            OnPropertyChanged();
            RebuildOptions();
        }
    }

    public MailListGroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            if (_groupMode == value)
            {
                return;
            }

            _groupMode = value;
            OnPropertyChanged();
            RebuildOptions();
        }
    }

    public bool IsThreadingEnabled
    {
        get => _isThreadingEnabled;
        set
        {
            if (_isThreadingEnabled == value)
            {
                return;
            }

            _isThreadingEnabled = value;
            OnPropertyChanged();
            RebuildOptions();
        }
    }

    public bool IsPinnedFirst
    {
        get => _isPinnedFirst;
        set
        {
            if (_isPinnedFirst == value)
            {
                return;
            }

            _isPinnedFirst = value;
            OnPropertyChanged();
            RebuildOptions();
        }
    }

    public int ItemCount => Items.Count;

    // ---- Single item ----

    public MailListLabItem AddSingle()
    {
        var item = CreateItem(NextThreadId(), _seedTime.AddMinutes(_sequence), "A new standalone message");
        Items.Add(item);
        return item;
    }

    /// <summary>Adds a newer message to the conversation of <paramref name="anchor"/>. A single becomes a thread.</summary>
    public MailListLabItem AddReply(MailListLabItem anchor, bool newer)
    {
        var createdAt = newer
            ? anchor.CreatedAt.AddMinutes(1 + _sequence % 7)
            : anchor.CreatedAt.AddHours(-(1 + _sequence % 5));
        var reply = CreateItem(anchor.ThreadId, createdAt, newer ? "Re: newer reply" : "Re: earlier message");
        reply.Subject = $"Re: {anchor.Subject}";
        Items.Add(reply);
        return reply;
    }

    public void UpdateSubject(MailListLabItem item)
    {
        item.Revision++;
        item.Subject = $"{StripRevision(item.Subject)} (edited {item.Revision})";
    }

    /// <summary>Moves the message one day back, which changes its date group and its thread position.</summary>
    public void MoveToPreviousDay(MailListLabItem item)
    {
        item.Revision++;
        item.CreatedAt = item.CreatedAt.AddDays(-1);
    }

    /// <summary>Renames the sender, which changes the name sort key and the name group.</summary>
    public void RenameSender(MailListLabItem item)
    {
        item.Revision++;
        var next = Senders[(Array.IndexOf(Senders, item.Sender) + 1 + Senders.Length) % Senders.Length];
        item.Sender = next;
        item.SenderAddress = ToAddress(next);
    }

    public void TogglePin(MailListLabItem item) => item.IsPinned = !item.IsPinned;

    public void ToggleRead(MailListLabItem item) => item.IsRead = !item.IsRead;

    public int Remove(IEnumerable<Guid> ids) => Items.RemoveRangeById(ids);

    // ---- Threads ----

    public string AddThread(int messageCount = 3)
    {
        var threadId = NextThreadId();
        var newest = _seedTime.AddMinutes(_sequence);
        var additions = new List<MailListLabItem>(messageCount);
        for (var index = 0; index < messageCount; index++)
        {
            var item = CreateItem(threadId, newest.AddHours(-index * 3), index == 0 ? "Latest message of a new thread" : $"Reply {messageCount - index}");
            item.Subject = index == 0 ? item.Subject : $"Re: {item.Subject}";
            additions.Add(item);
        }

        Items.AddRange(additions);
        return threadId;
    }

    public int RemoveThreads(IEnumerable<string> threadIds)
    {
        var keys = threadIds.ToHashSet(StringComparer.Ordinal);
        var ids = ((IEnumerable<MailListLabItem>)Items)
            .Where(item => item.ThreadId is not null && keys.Contains(item.ThreadId))
            .Select(item => item.StableId)
            .ToArray();
        return Items.RemoveRangeById(ids);
    }

    // ---- Bulk ----

    public void BulkAdd(int singles, int threads)
    {
        var additions = new List<MailListLabItem>(singles + threads * 3);
        for (var index = 0; index < singles; index++)
        {
            additions.Add(CreateItem(NextThreadId(), _seedTime.AddDays(-(index % 9)).AddMinutes(-index * 11), $"Bulk single {index + 1}"));
        }

        for (var index = 0; index < threads; index++)
        {
            var threadId = NextThreadId();
            var newest = _seedTime.AddDays(-(index % 6)).AddMinutes(-index * 17);
            for (var reply = 0; reply < 3; reply++)
            {
                additions.Add(CreateItem(threadId, newest.AddHours(-reply * 5), reply == 0 ? $"Bulk thread {index + 1}" : $"Re: bulk thread {index + 1}"));
            }
        }

        Items.AddRange(additions);
    }

    /// <summary>Removes every other row in storage order: a mix of singles, heads and leaves.</summary>
    public int RemoveEveryOther()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < Items.Count; index += 2)
        {
            ids.Add(Items[index].StableId);
        }

        return Items.RemoveRangeById(ids);
    }

    public int RemoveOldest(int count)
    {
        var ids = ((IEnumerable<MailListLabItem>)Items)
            .OrderBy(item => item.CreatedAt)
            .Take(count)
            .Select(item => item.StableId)
            .ToArray();
        return Items.RemoveRangeById(ids);
    }

    public void Reset()
    {
        _sequence = 0;
        _removalSequence = 0;
        using (Items.DeferRefresh())
        {
            Items.ReplaceAll(CreateSeed());
        }
    }

    public void Clear()
    {
        using (Items.DeferRefresh())
        {
            Items.Clear();
        }
    }

    // ---- Helpers ----

    private void Seed() => Items.AddRange(CreateSeed());

    private List<MailListLabItem> CreateSeed()
    {
        var items = new List<MailListLabItem>();
        var today = _seedTime.Date;

        // Two pinned singles.
        var pinned = CreateItem(NextThreadId(), today.AddHours(9), "Pinned: quarterly numbers");
        pinned.IsPinned = true;
        items.Add(pinned);
        var pinnedThreadId = NextThreadId();
        var pinnedHead = CreateItem(pinnedThreadId, today.AddHours(10), "Pinned thread: launch plan");
        pinnedHead.IsPinned = true;
        items.Add(pinnedHead);
        items.Add(CreateItem(pinnedThreadId, today.AddHours(8), "Re: launch plan"));

        // Today: a mix of singles and one longer thread.
        var longThread = NextThreadId();
        for (var index = 0; index < 5; index++)
        {
            items.Add(CreateItem(longThread, today.AddHours(16).AddMinutes(-index * 40), index == 0 ? "Thread of five: latest" : $"Re: thread of five ({5 - index})"));
        }

        for (var index = 0; index < 6; index++)
        {
            items.Add(CreateItem(NextThreadId(), today.AddHours(15).AddMinutes(-index * 25), $"Today single {index + 1}"));
        }

        // Yesterday: two-message threads that collapse to singles when a leaf is removed.
        var yesterday = today.AddDays(-1);
        for (var index = 0; index < 4; index++)
        {
            var threadId = NextThreadId();
            items.Add(CreateItem(threadId, yesterday.AddHours(14).AddMinutes(-index * 30), $"Pair thread {index + 1}: latest"));
            items.Add(CreateItem(threadId, yesterday.AddHours(9).AddMinutes(-index * 30), $"Re: pair thread {index + 1}"));
        }

        // Older days: singles spread across a week, plus a few three-message threads.
        for (var day = 2; day < 9; day++)
        {
            for (var index = 0; index < 5; index++)
            {
                items.Add(CreateItem(NextThreadId(), today.AddDays(-day).AddHours(17).AddMinutes(-index * 35), $"Day -{day} single {index + 1}"));
            }

            if (day % 2 == 0)
            {
                var threadId = NextThreadId();
                for (var reply = 0; reply < 3; reply++)
                {
                    items.Add(CreateItem(threadId, today.AddDays(-day).AddHours(12).AddHours(-reply * 2), reply == 0 ? $"Day -{day} thread: latest" : $"Re: day -{day} thread"));
                }
            }
        }

        return items;
    }

    private MailListLabItem CreateItem(string? threadId, DateTime createdAt, string subject)
    {
        var sequence = _sequence++;
        var sender = Senders[sequence % Senders.Length];
        var item = new MailListLabItem(
            threadId,
            createdAt,
            sender,
            ToAddress(sender),
            subject,
            $"{Subjects[sequence % Subjects.Length]} · message #{sequence + 1:000}")
        {
            IsRead = sequence % 3 == 0,
        };
        return item;
    }

    private string NextThreadId() => $"T-{++_removalSequence:000}";

    private static string ToAddress(string sender) => sender.ToLowerInvariant().Replace(' ', '.') + "@example.com";

    private static string StripRevision(string subject)
    {
        var index = subject.IndexOf(" (edited", StringComparison.Ordinal);
        return index < 0 ? subject : subject[..index];
    }

    private void RebuildOptions() =>
        ProjectionOptions = new MailListProjectionOptions
        {
            SortMode = _sortMode,
            GroupMode = _groupMode,
            IsThreadingEnabled = _isThreadingEnabled,
            IsPinnedFirst = _isPinnedFirst,
        };

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var entry = args.Action switch
        {
            NotifyCollectionChangedAction.Add => $"Add ×{args.NewItems?.Count ?? 0}",
            NotifyCollectionChangedAction.Remove => $"Remove ×{args.OldItems?.Count ?? 0}",
            NotifyCollectionChangedAction.Reset => "Reset",
            _ => args.Action.ToString(),
        };
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {entry} → {Items.Count} items");
        while (Log.Count > 40)
        {
            Log.RemoveAt(Log.Count - 1);
        }

        OnPropertyChanged(nameof(ItemCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
