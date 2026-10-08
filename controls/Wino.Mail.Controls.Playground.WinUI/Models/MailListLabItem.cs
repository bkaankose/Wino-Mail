using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wino.Mail.Controls.Core;
using WinRT;

namespace Wino.Mail.Controls.Playground.Models;

/// <summary>
/// Mutable sample mail for the mail list interactions page. Every key the projection sorts,
/// groups or threads by can change at runtime so the page can exercise updates in place.
/// </summary>
[GeneratedBindableCustomProperty]
public sealed partial class MailListLabItem : IMailListSourceItem, IContactPicture
{
    private string? _threadId;
    private DateTime _createdAt;
    private string _sender;
    private string _senderAddress;
    private string _subject;
    private string _preview;
    private bool _isPinned;
    private bool _isRead;
    private int _revision;

    public MailListLabItem(
        string? threadId,
        DateTime createdAt,
        string sender,
        string senderAddress,
        string subject,
        string preview)
    {
        StableId = Guid.NewGuid();
        _threadId = threadId;
        _createdAt = createdAt;
        _sender = sender;
        _senderAddress = senderAddress;
        _subject = subject;
        _preview = preview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid StableId { get; }

    public string? ThreadKey => _threadId;

    public string? ThreadId
    {
        get => _threadId;
        set => Set(ref _threadId, value, nameof(ThreadId), nameof(ThreadKey));
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => Set(ref _createdAt, value, nameof(CreatedAt), nameof(DateSortKey), nameof(DateText));
    }

    public DateTimeOffset DateSortKey => new(_createdAt);

    public string DateText => _createdAt.ToString("ddd HH:mm");

    public string Sender
    {
        get => _sender;
        set => Set(ref _sender, value, nameof(Sender), nameof(NameSortKey), nameof(Name));
    }

    public string NameSortKey => _sender;

    public string SenderAddress
    {
        get => _senderAddress;
        set => Set(ref _senderAddress, value, nameof(SenderAddress), nameof(Address));
    }

    public string Subject
    {
        get => _subject;
        set => Set(ref _subject, value, nameof(Subject));
    }

    public string Preview
    {
        get => _preview;
        set => Set(ref _preview, value, nameof(Preview));
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value, nameof(IsPinned));
    }

    public bool IsRead
    {
        get => _isRead;
        set => Set(ref _isRead, value, nameof(IsRead), nameof(UnreadOpacity));
    }

    public double UnreadOpacity => _isRead ? 0 : 1;

    /// <summary>How many times the item was updated in place. Shown on the row.</summary>
    public int Revision
    {
        get => _revision;
        set => Set(ref _revision, value, nameof(Revision), nameof(RevisionText));
    }

    public string RevisionText => _revision == 0 ? string.Empty : $"rev {_revision}";

    public string Name => _sender;

    public string Address => _senderAddress;

    public string? LocalImagePath => null;

    private void Set<T>(ref T field, T value, params string[] propertyNames)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        foreach (var propertyName in propertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
