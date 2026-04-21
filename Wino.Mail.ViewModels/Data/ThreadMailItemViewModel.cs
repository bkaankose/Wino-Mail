using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Thread mail item (multiple IMailItem) view model representation.
/// </summary>
public partial class ThreadMailItemViewModel : ObservableRecipient, IMailListItem, IMailItemDisplayInformation
{
    private readonly string _threadId;
    private readonly bool _isNewestEmailFirst;
    private readonly HashSet<Guid> _uniqueIdSet = [];
    private MailItemViewModel _cachedNewestMailViewModel;
    private int _suspendChildPropertyNotificationsCount;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsThreadExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Direct callback invoked when <see cref="IsSelected"/> changes.
    /// Used by the ListViewItem container to update its IsCustomSelected DP
    /// without subscribing to INotifyPropertyChanged (faster, AOT-safe).
    /// </summary>
    public Action<bool> OnSelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke(value);

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsSelectedOrExpanded => IsSelected || IsThreadExpanded;

    /// <summary>
    /// Gets the number of emails in this thread
    /// </summary>
    public int EmailCount => ThreadEmails.Count;

    /// <summary>
    /// Gets the latest email's subject for display
    /// </summary>
    public string Subject => newestMailViewModel?.MailCopy?.Subject;

    /// <summary>
    /// Gets the latest email's sender name for display
    /// </summary>
    public string FromName => newestMailViewModel?.MailCopy?.FromName ?? Translator.UnknownSender;

    /// <summary>
    /// Gets the latest email's creation date for sorting
    /// </summary>
    public DateTime CreationDate => newestMailViewModel?.MailCopy?.CreationDate ?? DateTime.MinValue;

    /// <summary>
    /// Gets the latest email's sender address for display
    /// </summary>
    public string FromAddress => newestMailViewModel?.FromAddress ?? string.Empty;

    /// <summary>
    /// Gets the preview text from the latest email
    /// </summary>
    public string PreviewText => newestMailViewModel?.PreviewText ?? string.Empty;

    /// <summary>
    /// Gets whether any email in this thread has attachments
    /// </summary>
    public bool HasAttachments => ThreadEmails.Any(e => e.HasAttachments);

    /// <summary>
    /// Gets whether any email in this thread is a calendar invitation.
    /// </summary>
    public bool IsCalendarEvent => ThreadEmails.Any(e => e.IsCalendarEvent);

    /// <summary>
    /// Gets whether any email in this thread is flagged
    /// </summary>
    public bool IsFlagged => ThreadEmails.Any(e => e.IsFlagged);

    /// <summary>
    /// Gets whether any email in this thread is pinned.
    /// </summary>
    public bool IsPinned => ThreadEmails.Any(e => e.IsPinned);

    /// <summary>
    /// Gets whether the latest email is focused
    /// </summary>
    public bool IsFocused => newestMailViewModel?.IsFocused ?? false;

    /// <summary>
    /// Gets whether all emails in this thread are read
    /// </summary>
    public bool IsRead => ThreadEmails.All(e => e.IsRead);

    public bool HasReadReceiptTracking => newestMailViewModel?.HasReadReceiptTracking ?? false;

    public bool IsReadReceiptAcknowledged => newestMailViewModel?.IsReadReceiptAcknowledged ?? false;

    public string ReadReceiptDisplayText => newestMailViewModel?.ReadReceiptDisplayText ?? string.Empty;
    public IReadOnlyList<MailCategory> Categories => ThreadEmails
        .SelectMany(a => a.Categories)
        .GroupBy(a => a.Id)
        .Select(a => a.First())
        .OrderBy(a => a.Name)
        .ToList();
    public bool HasCategories => ThreadEmails.Any(a => a.HasCategories);

    /// <summary>
    /// Gets whether any email in this thread is a draft
    /// </summary>
    public bool IsDraft => ThreadEmails.Any(e => e.IsDraft);

    /// <summary>
    /// Gets the draft ID from the latest email if it's a draft
    /// </summary>
    public string DraftId => newestMailViewModel?.DraftId ?? string.Empty;

    /// <summary>
    /// Gets the ID from the latest email
    /// </summary>
    public string Id => newestMailViewModel?.Id ?? string.Empty;

    /// <summary>
    /// Gets the importance of the latest email
    /// </summary>
    public MailImportance Importance => newestMailViewModel?.Importance ?? MailImportance.Normal;

    /// <summary>
    /// Gets the thread ID from the latest email
    /// </summary>
    public string ThreadId => newestMailViewModel?.ThreadId ?? _threadId;

    /// <summary>
    /// Gets the message ID from the latest email
    /// </summary>
    public string MessageId => newestMailViewModel?.MessageId ?? string.Empty;

    /// <summary>
    /// Gets the references from the latest email
    /// </summary>
    public string References => newestMailViewModel?.References ?? string.Empty;

    /// <summary>
    /// Gets the in-reply-to from the latest email
    /// </summary>
    public string InReplyTo => newestMailViewModel?.InReplyTo ?? string.Empty;

    /// <summary>
    /// Gets the file ID from the latest email
    /// </summary>
    public Guid FileId => newestMailViewModel?.FileId ?? Guid.Empty;

    /// <summary>
    /// Gets the folder ID from the latest email
    /// </summary>
    public Guid FolderId => newestMailViewModel?.FolderId ?? Guid.Empty;

    /// <summary>
    /// Gets the unique ID from the latest email
    /// </summary>
    public Guid UniqueId => newestMailViewModel?.UniqueId ?? Guid.Empty;

    public Guid? ContactPictureFileId => newestMailViewModel?.MailCopy?.SenderContact?.ContactPictureFileId;

    public bool ThumbnailUpdatedEvent => newestMailViewModel?.ThumbnailUpdatedEvent ?? false;

    public AccountContact SenderContact => newestMailViewModel?.MailCopy?.SenderContact;

    /// <summary>
    /// Gets all emails in this thread (observable)
    /// </summary>
    ///
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmailCount))]
    [NotifyPropertyChangedFor(nameof(Subject))]
    [NotifyPropertyChangedFor(nameof(FromName))]
    [NotifyPropertyChangedFor(nameof(CreationDate))]
    [NotifyPropertyChangedFor(nameof(FromAddress))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    [NotifyPropertyChangedFor(nameof(IsCalendarEvent))]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(IsRead))]
    [NotifyPropertyChangedFor(nameof(HasReadReceiptTracking))]
    [NotifyPropertyChangedFor(nameof(IsReadReceiptAcknowledged))]
    [NotifyPropertyChangedFor(nameof(ReadReceiptDisplayText))]
    [NotifyPropertyChangedFor(nameof(IsDraft))]
    [NotifyPropertyChangedFor(nameof(DraftId))]
    [NotifyPropertyChangedFor(nameof(Id))]
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
    public partial ObservableCollection<MailItemViewModel> ThreadEmails { get; set; } = [];

    private MailItemViewModel newestMailViewModel => _cachedNewestMailViewModel;

    public DateTime SortingDate => CreationDate;

    public string SortingName => FromName;

    public ThreadMailItemViewModel(string threadId, bool isNewestEmailFirst)
    {
        _threadId = threadId;
        _isNewestEmailFirst = isNewestEmailFirst;
    }

    internal void SuspendChildPropertyNotifications() => _suspendChildPropertyNotificationsCount++;

    internal void ResumeChildPropertyNotifications()
    {
        if (_suspendChildPropertyNotificationsCount > 0)
        {
            _suspendChildPropertyNotificationsCount--;
        }
    }

    private void RefreshLatestMailCache()
    {
        _cachedNewestMailViewModel = ThreadEmails
            .OrderByDescending(static item => item.MailCopy.CreationDate)
            .FirstOrDefault();
    }

    public MailItemViewModel GetDefaultSelectedThreadEmail()
    {
        if (ThreadEmails.Count == 0)
        {
            return null;
        }

        return _isNewestEmailFirst ? ThreadEmails.FirstOrDefault() : ThreadEmails.LastOrDefault();
    }

    /// <summary>
    /// Adds an email to this thread
    /// </summary>
    public void AddEmail(MailItemViewModel email)
    {
        if (email.MailCopy.ThreadId != _threadId)
            throw new ArgumentException($"Email ThreadId '{email.MailCopy.ThreadId}' does not match expander ThreadId '{_threadId}'");

        // Insert email in sorted order by CreationDate based on the configured thread direction.
        var insertIndex = 0;
        for (int i = 0; i < ThreadEmails.Count; i++)
        {
            bool shouldInsertBefore = _isNewestEmailFirst
                ? ThreadEmails[i].MailCopy.CreationDate < email.MailCopy.CreationDate
                : ThreadEmails[i].MailCopy.CreationDate > email.MailCopy.CreationDate;

            if (shouldInsertBefore)
            {
                insertIndex = i;
                break;
            }
            insertIndex = i + 1;
        }

        ThreadEmails.Insert(insertIndex, email);
        email.PropertyChanged += ThreadEmailPropertyChanged;
        _uniqueIdSet.Add(email.MailCopy.UniqueId);
        RefreshLatestMailCache();
        OnPropertyChanged(nameof(EmailCount));
        NotifyMailItemUpdated(email, MailCopyChangeFlags.All);
    }

    /// <summary>
    /// Removes an email from this thread
    /// </summary>
    public void RemoveEmail(MailItemViewModel email)
    {
        if (ThreadEmails.Remove(email))
        {
            email.PropertyChanged -= ThreadEmailPropertyChanged;
            _uniqueIdSet.Remove(email.MailCopy.UniqueId);
            RefreshLatestMailCache();
            OnPropertyChanged(nameof(EmailCount));
            NotifyMailItemUpdated(email, MailCopyChangeFlags.All);
        }
    }

    public void UnregisterThreadEmailPropertyChangedHandlers()
    {
        foreach (var email in ThreadEmails)
        {
            email.PropertyChanged -= ThreadEmailPropertyChanged;
        }
    }


    private void ThreadEmailPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_suspendChildPropertyNotificationsCount > 0)
            return;

        if (sender is not MailItemViewModel updatedMailItem)
            return;

        if (e.PropertyName == nameof(MailItemViewModel.IsSelected) ||
            e.PropertyName == nameof(MailItemViewModel.IsDisplayedInThread) ||
            e.PropertyName == nameof(MailItemViewModel.IsBusy))
        {
            return;
        }

        if (e.PropertyName == nameof(MailItemViewModel.ThumbnailUpdatedEvent))
        {
            if (ReferenceEquals(updatedMailItem, newestMailViewModel))
            {
                OnPropertyChanged(nameof(ThumbnailUpdatedEvent));
            }

            return;
        }

        var changedFlags = string.IsNullOrEmpty(e.PropertyName)
            ? MailCopyChangeFlags.All
            : MailItemViewModel.GetChangeFlagsForProperty(e.PropertyName);

        if (changedFlags == MailCopyChangeFlags.None)
        {
            NotifyMailItemUpdated(updatedMailItem, MailCopyChangeFlags.All);
            return;
        }

        NotifyMailItemUpdated(updatedMailItem, changedFlags);
    }

    /// <summary>
    /// Notifies that a mail item within this thread has been updated.
    /// </summary>
    /// <param name="updatedMailItem">The mail item that was updated (can be null to refresh all).</param>
    /// <param name="changedFlags">Set of changed child fields.</param>
    public void NotifyMailItemUpdated(MailItemViewModel updatedMailItem, MailCopyChangeFlags changedFlags = MailCopyChangeFlags.All)
    {
        if (changedFlags == MailCopyChangeFlags.None)
            return;

        var previousLatest = newestMailViewModel;

        if (changedFlags == MailCopyChangeFlags.All ||
            (changedFlags & MailCopyChangeFlags.CreationDate) != 0 ||
            previousLatest == null ||
            !ThreadEmails.Contains(previousLatest))
        {
            RefreshLatestMailCache();
        }

        var currentLatest = newestMailViewModel;
        var latestChanged = !ReferenceEquals(previousLatest, currentLatest);

        var updatesDisplayedLatest = changedFlags == MailCopyChangeFlags.All ||
                                     updatedMailItem == null ||
                                     latestChanged ||
                                     ReferenceEquals(updatedMailItem, previousLatest) ||
                                     ReferenceEquals(updatedMailItem, currentLatest);

        var changedProperties = new List<string>(10);

        void Queue(string propertyName)
        {
            if (!changedProperties.Contains(propertyName))
            {
                changedProperties.Add(propertyName);
            }
        }

        if (updatesDisplayedLatest)
        {
            if (changedFlags == MailCopyChangeFlags.All || latestChanged)
            {
                Queue(nameof(Subject));
                Queue(nameof(FromName));
                Queue(nameof(CreationDate));
                Queue(nameof(FromAddress));
                Queue(nameof(PreviewText));
                Queue(nameof(IsFocused));
                Queue(nameof(DraftId));
                Queue(nameof(Id));
                Queue(nameof(Importance));
                Queue(nameof(ThreadId));
                Queue(nameof(MessageId));
                Queue(nameof(References));
                Queue(nameof(InReplyTo));
                Queue(nameof(FileId));
                Queue(nameof(FolderId));
                Queue(nameof(UniqueId));
                Queue(nameof(ContactPictureFileId));
                Queue(nameof(SenderContact));
                Queue(nameof(ThumbnailUpdatedEvent));
                Queue(nameof(SortingDate));
                Queue(nameof(SortingName));
            }
            else
            {
                if ((changedFlags & MailCopyChangeFlags.Subject) != 0)
                    Queue(nameof(Subject));

                if ((changedFlags & MailCopyChangeFlags.FromName) != 0)
                {
                    Queue(nameof(FromName));
                    Queue(nameof(SortingName));
                }

                if ((changedFlags & MailCopyChangeFlags.CreationDate) != 0)
                {
                    Queue(nameof(CreationDate));
                    Queue(nameof(SortingDate));
                }

                if ((changedFlags & MailCopyChangeFlags.FromAddress) != 0)
                    Queue(nameof(FromAddress));

                if ((changedFlags & MailCopyChangeFlags.PreviewText) != 0)
                    Queue(nameof(PreviewText));

                if ((changedFlags & MailCopyChangeFlags.IsFocused) != 0)
                    Queue(nameof(IsFocused));

                if ((changedFlags & MailCopyChangeFlags.DraftId) != 0)
                    Queue(nameof(DraftId));

                if ((changedFlags & MailCopyChangeFlags.Id) != 0)
                    Queue(nameof(Id));

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
            }
        }

        if ((changedFlags & MailCopyChangeFlags.HasAttachments) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(HasAttachments));

        if ((changedFlags & MailCopyChangeFlags.ItemType) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(IsCalendarEvent));

        if ((changedFlags & MailCopyChangeFlags.IsFlagged) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(IsFlagged));

        if ((changedFlags & MailCopyChangeFlags.IsPinned) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(IsPinned));

        if ((changedFlags & MailCopyChangeFlags.IsRead) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(IsRead));

        if ((changedFlags & MailCopyChangeFlags.ReadReceiptState) != 0 || changedFlags == MailCopyChangeFlags.All)
        {
            Queue(nameof(HasReadReceiptTracking));
            Queue(nameof(IsReadReceiptAcknowledged));
            Queue(nameof(ReadReceiptDisplayText));
        }

        if ((changedFlags & MailCopyChangeFlags.IsDraft) != 0 || changedFlags == MailCopyChangeFlags.All)
            Queue(nameof(IsDraft));

        if ((changedFlags & MailCopyChangeFlags.Categories) != 0 || changedFlags == MailCopyChangeFlags.All)
        {
            Queue(nameof(Categories));
            Queue(nameof(HasCategories));
        }

        foreach (var changedProperty in changedProperties)
        {
            OnPropertyChanged(changedProperty);
        }
    }

    /// <summary>
    /// Checks if this thread contains an email with the specified unique ID
    /// </summary>
    public bool HasUniqueId(Guid uniqueId) => _uniqueIdSet.Contains(uniqueId);

    public IEnumerable<Guid> GetContainingIds() => ThreadEmails.Select(a => a.MailCopy.UniqueId);

    public IEnumerable<MailItemViewModel> GetSelectedMailItems()
    {
        if (IsSelected)
        {
            // If the thread itself is selected, return all emails in the thread
            return ThreadEmails;
        }
        else
        {
            // Otherwise, return only individually selected emails within the thread
            return ThreadEmails.Where(e => e.IsSelected);
        }
    }
}

