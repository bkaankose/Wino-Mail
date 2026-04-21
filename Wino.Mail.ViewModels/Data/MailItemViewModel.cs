using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Single view model for IMailItem representation.
/// </summary>
public partial class MailItemViewModel(MailCopy mailCopy) : ObservableRecipient, IMailListItem, IMailItemDisplayInformation
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreationDate))]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(FromName))]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(IsRead))]
    [NotifyPropertyChangedFor(nameof(IsDraft))]
    [NotifyPropertyChangedFor(nameof(DraftId))]
    [NotifyPropertyChangedFor(nameof(Id))]
    [NotifyPropertyChangedFor(nameof(Subject))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    [NotifyPropertyChangedFor(nameof(FromAddress))]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    [NotifyPropertyChangedFor(nameof(IsCalendarEvent))]
    [NotifyPropertyChangedFor(nameof(Importance))]
    [NotifyPropertyChangedFor(nameof(ThreadId))]
    [NotifyPropertyChangedFor(nameof(MessageId))]
    [NotifyPropertyChangedFor(nameof(References))]
    [NotifyPropertyChangedFor(nameof(InReplyTo))]
    [NotifyPropertyChangedFor(nameof(FileId))]
    [NotifyPropertyChangedFor(nameof(FolderId))]
    [NotifyPropertyChangedFor(nameof(UniqueId))]
    [NotifyPropertyChangedFor(nameof(ContactPictureFileId))]
    [NotifyPropertyChangedFor(nameof(SenderContact))]
    [NotifyPropertyChangedFor(nameof(Categories))]
    [NotifyPropertyChangedFor(nameof(HasCategories))]
    public partial MailCopy MailCopy { get; set; } = mailCopy;

    [ObservableProperty]
    public partial bool IsDisplayedInThread { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Direct callback invoked when <see cref="IsSelected"/> changes.
    /// Used by the ListViewItem container to update its IsCustomSelected DP
    /// without subscribing to INotifyPropertyChanged (faster, AOT-safe).
    /// </summary>
    public Action<bool> OnSelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke(value);

    /// <summary>
    /// Indicates if this mail item is currently being processed by a network operation.
    /// Used to show loading state in the UI.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsThreadExpanded => false;

    public AccountContact SenderContact => MailCopy.SenderContact;

    public DateTime CreationDate
    {
        get => MailCopy.CreationDate;
        set => SetProperty(MailCopy.CreationDate, value, MailCopy, (u, n) => u.CreationDate = n);
    }

    [ObservableProperty]
    public partial bool ThumbnailUpdatedEvent { get; set; } = false;

    public bool IsFlagged
    {
        get => MailCopy.IsFlagged;
        set => SetProperty(MailCopy.IsFlagged, value, MailCopy, (u, n) => u.IsFlagged = n);
    }

    public bool IsPinned
    {
        get => MailCopy.IsPinned;
        set => SetProperty(MailCopy.IsPinned, value, MailCopy, (u, n) => u.IsPinned = n);
    }

    public string FromName
    {
        get => string.IsNullOrEmpty(MailCopy.FromName) ? MailCopy.FromAddress : MailCopy.FromName;
        set => SetProperty(MailCopy.FromName, value, MailCopy, (u, n) => u.FromName = n);
    }

    public bool IsFocused
    {
        get => MailCopy.IsFocused;
        set => SetProperty(MailCopy.IsFocused, value, MailCopy, (u, n) => u.IsFocused = n);
    }

    public bool IsRead
    {
        get => MailCopy.IsRead;
        set => SetProperty(MailCopy.IsRead, value, MailCopy, (u, n) => u.IsRead = n);
    }

    public bool IsDraft
    {
        get => MailCopy.IsDraft;
        set => SetProperty(MailCopy.IsDraft, value, MailCopy, (u, n) => u.IsDraft = n);
    }

    public bool HasReadReceiptTracking => MailCopy.IsReadReceiptRequested;

    public bool IsReadReceiptAcknowledged => MailCopy.ReadReceiptStatus == SentMailReceiptStatus.Acknowledged;

    public string ReadReceiptDisplayText => MailCopy.ReadReceiptStatus switch
    {
        SentMailReceiptStatus.Acknowledged => Translator.MailReceiptStatus_Acknowledged,
        SentMailReceiptStatus.Requested => Translator.MailReceiptStatus_Requested,
        _ => string.Empty
    };

    public IReadOnlyList<MailCategory> Categories => MailCopy.Categories;

    public bool HasCategories => Categories.Count > 0;

    public string DraftId
    {
        get => MailCopy.DraftId;
        set => SetProperty(MailCopy.DraftId, value, MailCopy, (u, n) => u.DraftId = n);
    }

    public string Id
    {
        get => MailCopy.Id;
        set => SetProperty(MailCopy.Id, value, MailCopy, (u, n) => u.Id = n);
    }

    public string Subject
    {
        get => MailCopy.Subject;
        set => SetProperty(MailCopy.Subject, value, MailCopy, (u, n) => u.Subject = n);
    }

    public string PreviewText
    {
        get => MailCopy.PreviewText;
        set => SetProperty(MailCopy.PreviewText, value, MailCopy, (u, n) => u.PreviewText = n);
    }

    public string FromAddress
    {
        get => MailCopy.FromAddress;
        set => SetProperty(MailCopy.FromAddress, value, MailCopy, (u, n) => u.FromAddress = n);
    }

    public bool HasAttachments
    {
        get => MailCopy.HasAttachments;
        set => SetProperty(MailCopy.HasAttachments, value, MailCopy, (u, n) => u.HasAttachments = n);
    }

    public bool IsCalendarEvent => MailCopy.ItemType == MailItemType.CalendarInvitation;

    public MailImportance Importance
    {
        get => MailCopy.Importance;
        set => SetProperty(MailCopy.Importance, value, MailCopy, (u, n) => u.Importance = n);
    }

    public string ThreadId
    {
        get => MailCopy.ThreadId;
        set => SetProperty(MailCopy.ThreadId, value, MailCopy, (u, n) => u.ThreadId = n);
    }

    public string MessageId
    {
        get => MailCopy.MessageId;
        set => SetProperty(MailCopy.MessageId, value, MailCopy, (u, n) => u.MessageId = n);
    }

    public string References
    {
        get => MailCopy.References;
        set => SetProperty(MailCopy.References, value, MailCopy, (u, n) => u.References = n);
    }

    public string InReplyTo
    {
        get => MailCopy.InReplyTo;
        set => SetProperty(MailCopy.InReplyTo, value, MailCopy, (u, n) => u.InReplyTo = n);
    }

    public Guid FileId
    {
        get => MailCopy.FileId;
        set => SetProperty(MailCopy.FileId, value, MailCopy, (u, n) => u.FileId = n);
    }

    public Guid FolderId
    {
        get => MailCopy.FolderId;
        set => SetProperty(MailCopy.FolderId, value, MailCopy, (u, n) => u.FolderId = n);
    }

    public Guid UniqueId
    {
        get => MailCopy.UniqueId;
        set => SetProperty(MailCopy.UniqueId, value, MailCopy, (u, n) => u.UniqueId = n);
    }

    public Guid? ContactPictureFileId
    {
        get => MailCopy.SenderContact?.ContactPictureFileId;
        set => SetProperty(MailCopy.SenderContact?.ContactPictureFileId, value, MailCopy, (u, n) =>
        {
            if (u.SenderContact != null)
                u.SenderContact.ContactPictureFileId = n;
        });
    }

    public DateTime SortingDate => CreationDate;

    public string SortingName => FromName;

    public IEnumerable<Guid> GetContainingIds() => [MailCopy.UniqueId];

    public IEnumerable<MailItemViewModel> GetSelectedMailItems()
    {
        if (IsSelected)
        {
            yield return this;
        }
    }

    public static MailCopyChangeFlags GetChangeFlagsForProperty(string propertyName)
    {
        return propertyName switch
        {
            nameof(CreationDate) or nameof(SortingDate) => MailCopyChangeFlags.CreationDate,
            nameof(IsFlagged) => MailCopyChangeFlags.IsFlagged,
            nameof(IsPinned) => MailCopyChangeFlags.IsPinned,
            nameof(FromName) or nameof(SortingName) => MailCopyChangeFlags.FromName,
            nameof(IsFocused) => MailCopyChangeFlags.IsFocused,
            nameof(IsRead) => MailCopyChangeFlags.IsRead,
            nameof(IsDraft) => MailCopyChangeFlags.IsDraft,
            nameof(HasReadReceiptTracking) or nameof(IsReadReceiptAcknowledged) or nameof(ReadReceiptDisplayText) => MailCopyChangeFlags.ReadReceiptState,
            nameof(DraftId) => MailCopyChangeFlags.DraftId,
            nameof(Id) => MailCopyChangeFlags.Id,
            nameof(Subject) => MailCopyChangeFlags.Subject,
            nameof(PreviewText) => MailCopyChangeFlags.PreviewText,
            nameof(FromAddress) => MailCopyChangeFlags.FromAddress,
            nameof(HasAttachments) => MailCopyChangeFlags.HasAttachments,
            nameof(IsCalendarEvent) => MailCopyChangeFlags.ItemType,
            nameof(Importance) => MailCopyChangeFlags.Importance,
            nameof(ThreadId) => MailCopyChangeFlags.ThreadId,
            nameof(MessageId) => MailCopyChangeFlags.MessageId,
            nameof(References) => MailCopyChangeFlags.References,
            nameof(InReplyTo) => MailCopyChangeFlags.InReplyTo,
            nameof(FileId) => MailCopyChangeFlags.FileId,
            nameof(FolderId) => MailCopyChangeFlags.FolderId,
            nameof(UniqueId) => MailCopyChangeFlags.UniqueId,
            nameof(ContactPictureFileId) or nameof(SenderContact) => MailCopyChangeFlags.SenderContact,
            nameof(Categories) or nameof(HasCategories) => MailCopyChangeFlags.Categories,
            _ => MailCopyChangeFlags.None
        };
    }

    /// <summary>
    /// Updates the existing <see cref="MailCopy"/> while raising only the relevant UI notifications.
    /// </summary>
    /// <param name="source">Source data used to update this item.</param>
    /// <param name="changeHint">
    /// Optional set of known changes. This is required when <paramref name="source"/> is the same instance
    /// and has already been mutated by Apply/Revert flows.
    /// </param>
    /// <returns>The effective set of changed fields used for notifications.</returns>
    public MailCopyChangeFlags UpdateFrom(MailCopy source, MailCopyChangeFlags changeHint = MailCopyChangeFlags.None)
    {
        if (source == null) return MailCopyChangeFlags.None;

        var changedFlags = MailCopyChangeFlags.None;
        var isSameReference = ReferenceEquals(MailCopy, source);

        if (!isSameReference)
        {
            changedFlags |= SetIfChanged(MailCopy.Id, source.Id, value => MailCopy.Id = value, MailCopyChangeFlags.Id);
            changedFlags |= SetIfChanged(MailCopy.FolderId, source.FolderId, value => MailCopy.FolderId = value, MailCopyChangeFlags.FolderId);
            changedFlags |= SetIfChanged(MailCopy.ThreadId, source.ThreadId, value => MailCopy.ThreadId = value, MailCopyChangeFlags.ThreadId);
            changedFlags |= SetIfChanged(MailCopy.MessageId, source.MessageId, value => MailCopy.MessageId = value, MailCopyChangeFlags.MessageId);
            changedFlags |= SetIfChanged(MailCopy.References, source.References, value => MailCopy.References = value, MailCopyChangeFlags.References);
            changedFlags |= SetIfChanged(MailCopy.InReplyTo, source.InReplyTo, value => MailCopy.InReplyTo = value, MailCopyChangeFlags.InReplyTo);
            changedFlags |= SetIfChanged(MailCopy.IsDraft, source.IsDraft, value => MailCopy.IsDraft = value, MailCopyChangeFlags.IsDraft);
            changedFlags |= SetIfChanged(MailCopy.DraftId, source.DraftId, value => MailCopy.DraftId = value, MailCopyChangeFlags.DraftId);
            changedFlags |= SetIfChanged(MailCopy.CreationDate, source.CreationDate, value => MailCopy.CreationDate = value, MailCopyChangeFlags.CreationDate);
            changedFlags |= SetIfChanged(MailCopy.Subject, source.Subject, value => MailCopy.Subject = value, MailCopyChangeFlags.Subject);
            changedFlags |= SetIfChanged(MailCopy.PreviewText, source.PreviewText, value => MailCopy.PreviewText = value, MailCopyChangeFlags.PreviewText);
            changedFlags |= SetIfChanged(MailCopy.FromName, source.FromName, value => MailCopy.FromName = value, MailCopyChangeFlags.FromName);
            changedFlags |= SetIfChanged(MailCopy.FromAddress, source.FromAddress, value => MailCopy.FromAddress = value, MailCopyChangeFlags.FromAddress);
            changedFlags |= SetIfChanged(MailCopy.HasAttachments, source.HasAttachments, value => MailCopy.HasAttachments = value, MailCopyChangeFlags.HasAttachments);
            changedFlags |= SetIfChanged(MailCopy.Importance, source.Importance, value => MailCopy.Importance = value, MailCopyChangeFlags.Importance);
            changedFlags |= SetIfChanged(MailCopy.IsRead, source.IsRead, value => MailCopy.IsRead = value, MailCopyChangeFlags.IsRead);
            changedFlags |= SetIfChanged(MailCopy.IsFlagged, source.IsFlagged, value => MailCopy.IsFlagged = value, MailCopyChangeFlags.IsFlagged);
            changedFlags |= SetIfChanged(MailCopy.IsPinned, source.IsPinned, value => MailCopy.IsPinned = value, MailCopyChangeFlags.IsPinned);
            changedFlags |= SetIfChanged(MailCopy.IsFocused, source.IsFocused, value => MailCopy.IsFocused = value, MailCopyChangeFlags.IsFocused);
            changedFlags |= SetIfChanged(MailCopy.FileId, source.FileId, value => MailCopy.FileId = value, MailCopyChangeFlags.FileId);
            changedFlags |= SetIfChanged(MailCopy.ItemType, source.ItemType, value => MailCopy.ItemType = value, MailCopyChangeFlags.ItemType);
            changedFlags |= SetIfChangedIfNotNull(MailCopy.SenderContact, source.SenderContact, value => MailCopy.SenderContact = value, MailCopyChangeFlags.SenderContact);
            changedFlags |= SetIfChangedIfNotNull(MailCopy.AssignedAccount, source.AssignedAccount, value => MailCopy.AssignedAccount = value, MailCopyChangeFlags.AssignedAccount);
            changedFlags |= SetIfChangedIfNotNull(MailCopy.AssignedFolder, source.AssignedFolder, value => MailCopy.AssignedFolder = value, MailCopyChangeFlags.AssignedFolder);
            changedFlags |= SetIfChanged(MailCopy.UniqueId, source.UniqueId, value => MailCopy.UniqueId = value, MailCopyChangeFlags.UniqueId);
            changedFlags |= SetIfChanged(MailCopy.IsReadReceiptRequested, source.IsReadReceiptRequested, value => MailCopy.IsReadReceiptRequested = value, MailCopyChangeFlags.ReadReceiptState);
            changedFlags |= SetIfChanged(MailCopy.ReadReceiptStatus, source.ReadReceiptStatus, value => MailCopy.ReadReceiptStatus = value, MailCopyChangeFlags.ReadReceiptState);
            changedFlags |= SetIfChanged(MailCopy.ReadReceiptAcknowledgedAtUtc, source.ReadReceiptAcknowledgedAtUtc, value => MailCopy.ReadReceiptAcknowledgedAtUtc = value, MailCopyChangeFlags.ReadReceiptState);
            changedFlags |= SetIfChanged(MailCopy.ReadReceiptMessageUniqueId, source.ReadReceiptMessageUniqueId, value => MailCopy.ReadReceiptMessageUniqueId = value, MailCopyChangeFlags.ReadReceiptState);
        }

        changedFlags |= changeHint;

        if (isSameReference && changedFlags == MailCopyChangeFlags.None)
        {
            // Without a hint there is no reliable way to diff in-place updates on the same instance.
            // Fall back to full refresh to preserve correctness.
            changedFlags = MailCopyChangeFlags.All;
        }

        RaisePropertyChanges(changedFlags);

        return changedFlags;
    }

    public MailCopyChangeFlags ApplyStateChanges(bool? isRead = null, bool? isFlagged = null)
    {
        var changedFlags = MailCopyChangeFlags.None;

        if (isRead.HasValue && MailCopy.IsRead != isRead.Value)
        {
            MailCopy.IsRead = isRead.Value;
            changedFlags |= MailCopyChangeFlags.IsRead;
        }

        if (isFlagged.HasValue && MailCopy.IsFlagged != isFlagged.Value)
        {
            MailCopy.IsFlagged = isFlagged.Value;
            changedFlags |= MailCopyChangeFlags.IsFlagged;
        }

        if (changedFlags != MailCopyChangeFlags.None)
        {
            RaisePropertyChanges(changedFlags);
        }

        return changedFlags;
    }

    private static MailCopyChangeFlags SetIfChanged<T>(T currentValue, T newValue, Action<T> setter, MailCopyChangeFlags flag)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue, newValue))
            return MailCopyChangeFlags.None;

        setter(newValue);
        return flag;
    }

    private static MailCopyChangeFlags SetIfChangedIfNotNull<T>(T currentValue, T newValue, Action<T> setter, MailCopyChangeFlags flag) where T : class
    {
        if (newValue == null)
            return MailCopyChangeFlags.None;

        return SetIfChanged(currentValue, newValue, setter, flag);
    }

    private void RaisePropertyChanges(MailCopyChangeFlags changedFlags)
    {
        if (changedFlags == MailCopyChangeFlags.None)
            return;

        var changedProperties = new List<string>(12);

        void Queue(string propertyName)
        {
            if (!changedProperties.Contains(propertyName))
            {
                changedProperties.Add(propertyName);
            }
        }

        if ((changedFlags & MailCopyChangeFlags.CreationDate) != 0)
        {
            Queue(nameof(CreationDate));
            Queue(nameof(SortingDate));
        }

        if ((changedFlags & MailCopyChangeFlags.IsFlagged) != 0)
            Queue(nameof(IsFlagged));

        if ((changedFlags & MailCopyChangeFlags.IsPinned) != 0)
            Queue(nameof(IsPinned));

        if ((changedFlags & MailCopyChangeFlags.FromName) != 0)
        {
            Queue(nameof(FromName));
            Queue(nameof(SortingName));
        }

        if ((changedFlags & MailCopyChangeFlags.FromAddress) != 0)
        {
            Queue(nameof(FromAddress));
            Queue(nameof(FromName));
            Queue(nameof(SortingName));
        }

        if ((changedFlags & MailCopyChangeFlags.IsFocused) != 0)
            Queue(nameof(IsFocused));

        if ((changedFlags & MailCopyChangeFlags.IsRead) != 0)
            Queue(nameof(IsRead));

        if ((changedFlags & MailCopyChangeFlags.IsDraft) != 0)
            Queue(nameof(IsDraft));

        if ((changedFlags & MailCopyChangeFlags.ReadReceiptState) != 0)
        {
            Queue(nameof(HasReadReceiptTracking));
            Queue(nameof(IsReadReceiptAcknowledged));
            Queue(nameof(ReadReceiptDisplayText));
        }

        if ((changedFlags & MailCopyChangeFlags.DraftId) != 0)
            Queue(nameof(DraftId));

        if ((changedFlags & MailCopyChangeFlags.Id) != 0)
            Queue(nameof(Id));

        if ((changedFlags & MailCopyChangeFlags.Subject) != 0)
            Queue(nameof(Subject));

        if ((changedFlags & MailCopyChangeFlags.PreviewText) != 0)
            Queue(nameof(PreviewText));

        if ((changedFlags & MailCopyChangeFlags.HasAttachments) != 0)
            Queue(nameof(HasAttachments));

        if ((changedFlags & MailCopyChangeFlags.ItemType) != 0)
            Queue(nameof(IsCalendarEvent));

        if ((changedFlags & MailCopyChangeFlags.Importance) != 0)
            Queue(nameof(Importance));

        if ((changedFlags & MailCopyChangeFlags.ThreadId) != 0)
            Queue(nameof(ThreadId));

        if ((changedFlags & MailCopyChangeFlags.MessageId) != 0)
            Queue(nameof(MessageId));

        if ((changedFlags & MailCopyChangeFlags.References) != 0)
            Queue(nameof(References));

        if ((changedFlags & MailCopyChangeFlags.InReplyTo) != 0)
            Queue(nameof(InReplyTo));

        if ((changedFlags & MailCopyChangeFlags.FileId) != 0)
            Queue(nameof(FileId));

        if ((changedFlags & MailCopyChangeFlags.FolderId) != 0)
            Queue(nameof(FolderId));

        if ((changedFlags & MailCopyChangeFlags.UniqueId) != 0)
            Queue(nameof(UniqueId));

        if ((changedFlags & MailCopyChangeFlags.SenderContact) != 0)
        {
            Queue(nameof(ContactPictureFileId));
            Queue(nameof(SenderContact));
        }

        if ((changedFlags & MailCopyChangeFlags.Categories) != 0)
        {
            Queue(nameof(Categories));
            Queue(nameof(HasCategories));
        }

        foreach (var changedProperty in changedProperties)
        {
            OnPropertyChanged(changedProperty);
        }
    }

    public void UpdateCategories(IReadOnlyList<MailCategory> categories)
    {
        MailCopy.Categories = categories?.ToList() ?? [];
        RaisePropertyChanges(MailCopyChangeFlags.Categories);
    }
}
