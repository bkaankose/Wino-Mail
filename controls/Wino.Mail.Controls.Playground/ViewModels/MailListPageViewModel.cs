using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;

namespace Wino.Mail.Controls.Playground.ViewModels;

public sealed class MailListPageViewModel : INotifyPropertyChanged
{
    private int _messageSequence = 1;
    private int _singleSequence = 1;

    private string _newThreadId = "MyThread1";
    private string _newSender = "Avery Stone";
    private string _newSenderAddress = "avery.stone@example.com";
    private string _newSubject = "A new reply";
    private string _newPreview = "This message was added from the playground.";

    public string NewThreadId { get => _newThreadId; set => Set(ref _newThreadId, value); }
    public string NewSender { get => _newSender; set => Set(ref _newSender, value); }
    public string NewSenderAddress { get => _newSenderAddress; set => Set(ref _newSenderAddress, value); }
    public string NewSubject { get => _newSubject; set => Set(ref _newSubject, value); }
    public string NewPreview { get => _newPreview; set => Set(ref _newPreview, value); }

    public MailListCollection<MailListPlaygroundItem> Items { get; } = [];

    public MailListProjectionOptions ProjectionOptions { get; } = new()
    {
        GroupMode = MailListGroupMode.Date,
        IsThreadingEnabled = true,
        IsPinnedFirst = true,
        SortMode = MailListSortMode.Date,
    };

    public MailListPageViewModel()
    {
        SeedItems();
    }

    public void AddMail()
    {
        Items.Add(new MailListPlaygroundItem(
            string.IsNullOrWhiteSpace(NewThreadId) ? $"Thread-{_messageSequence}" : NewThreadId,
            DateTime.Now,
            NewSender,
            NewSubject,
            NewPreview,
            NewSenderAddress));
        NewSubject = $"Reply {_messageSequence++}";
    }

    public void AddToMyThread()
    {
        Items.Add(new MailListPlaygroundItem(
            "MyThread1",
            DateTime.Now,
            "Taylor Reed",
            $"MyThread1 reply {_messageSequence++}",
            "Adding this message demonstrates the single-to-thread transition.",
            "taylor.reed@example.com"));
    }

    public void AddStandalone()
    {
        var id = $"Solo-{_singleSequence++:000}";
        Items.Add(new MailListPlaygroundItem(id, DateTime.Now, "New correspondent", $"Standalone message in {id}", "A new independent conversation.", "new@example.com"));
    }

    public void Reset()
    {
        Items.Clear();
        _messageSequence = 1;
        _singleSequence = 1;
        SeedItems();
    }

    private void SeedItems()
    {
        var now = DateTime.Now;
        var items = new List<MailListPlaygroundItem>
        {
            new("MyThread1", now.AddMinutes(-12), "Morgan Lee", "MyThread1 starts as a single message", "Select this row, then add to MyThread1 to watch it become a thread.", "morgan@example.com"),
            new("ProjectPhoenix", now.AddMinutes(-28), "Riley Chen", "Phoenix: revised delivery plan", "Newest message keeps this thread in today's group.", "riley@example.com"),
            new("ProjectPhoenix", now.AddDays(-1).AddHours(-2), "Sam Rivera", "Phoenix: review notes", "An earlier reply shown when the thread expands.", "sam@example.com"),
            new("ProjectPhoenix", now.AddDays(-2).AddHours(-4), "Morgan Lee", "Phoenix: initial proposal", "The conversation began two days ago.", "morgan@example.com"),
            new("DesignReview", now.Date.AddDays(-1).AddHours(16), "Jamie Park", "Design review follow-up", "Two-message thread grouped under yesterday.", "jamie@example.com"),
            new("DesignReview", now.Date.AddDays(-1).AddHours(11), "Alex Kim", "Design review agenda", "The original agenda for yesterday's review.", "alex@example.com"),
        };

        var senders = new[] { "Avery Stone", "Jordan Blake", "Casey Morgan", "Robin Shah", "Drew Ellis", "Quinn Parker" };
        var subjects = new[] { "Weekly status update", "Invoice clarification", "Launch checklist", "Customer feedback", "Planning notes", "Follow-up required" };
        for (var index = 0; index < 120; index++)
        {
            items.Add(new MailListPlaygroundItem($"DemoSingle-{index + 1:000}", now.Date.AddDays(-(index % 21)).AddHours(8 + index % 9), senders[index % senders.Length], $"{subjects[index % subjects.Length]} #{index + 1:000}", $"Standalone demo message {index + 1:000}.", $"sender{index}@example.com"));
        }

        for (var index = 0; index < 40; index++)
        {
            var threadId = $"DemoThread-{index + 1:000}";
            var newest = now.Date.AddDays(-(index * 3 % 21)).AddHours(14);
            for (var reply = 0; reply < 3; reply++)
            {
                items.Add(new MailListPlaygroundItem(threadId, newest.AddDays(-reply).AddHours(-reply * 2), senders[(index + reply) % senders.Length], $"{subjects[(index + reply) % subjects.Length]} · {(reply == 0 ? "latest" : "reply")}", "A message inside a virtualized demo thread.", $"thread{index}-{reply}@example.com"));
            }
        }

        Items.AddRange(items);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
