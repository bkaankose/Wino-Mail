using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Thread mail item (multiple IMailItem) view model representation.
/// </summary>
public partial class ThreadMailItemViewModel : ObservableRecipient, IMailListItem
{
    private readonly string _threadId;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsThreadExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsSelected { get; set; }

    public bool IsSelectedOrExpanded => IsSelected || IsThreadExpanded;

    /// <summary>
    /// Gets the number of emails in this thread
    /// </summary>
    public int EmailCount => ThreadEmails.Count;

    /// <summary>
    /// Gets the latest email's subject for display
    /// </summary>
    public string Subject => latestMailViewModel?.MailCopy?.Subject;

    /// <summary>
    /// Gets the latest email's sender name for display
    /// </summary>
    public string FromName => latestMailViewModel?.MailCopy?.FromName ?? Translator.UnknownSender;

    /// <summary>
    /// Gets the latest email's creation date for sorting
    /// </summary>
    public DateTime CreationDate => latestMailViewModel?.MailCopy?.CreationDate ?? DateTime.MinValue;

    /// <summary>
    /// Gets the latest email's sender address for display
    /// </summary>
    public string FromAddress => latestMailViewModel?.FromAddress ?? string.Empty;

    /// <summary>
    /// Gets the preview text from the latest email
    /// </summary>
    public string PreviewText => latestMailViewModel?.PreviewText ?? string.Empty;

    /// <summary>
    /// Gets whether any email in this thread has attachments
    /// </summary>
    public bool HasAttachments => ThreadEmails.Any(e => e.HasAttachments);

    /// <summary>
    /// Gets whether any email in this thread is flagged
    /// </summary>
    public bool IsFlagged => ThreadEmails.Any(e => e.IsFlagged);

    /// <summary>
    /// Gets whether the latest email is focused
    /// </summary>
    public bool IsFocused => latestMailViewModel?.IsFocused ?? false;

    /// <summary>
    /// Gets whether all emails in this thread are read
    /// </summary>
    public bool IsRead => ThreadEmails.All(e => e.IsRead);

    /// <summary>
    /// Gets whether any email in this thread is a draft
    /// </summary>
    public bool IsDraft => ThreadEmails.Any(e => e.IsDraft);

    /// <summary>
    /// Gets the draft ID from the latest email if it's a draft
    /// </summary>
    public string DraftId => latestMailViewModel?.DraftId ?? string.Empty;

    /// <summary>
    /// Gets the ID from the latest email
    /// </summary>
    public string Id => latestMailViewModel?.Id ?? string.Empty;

    /// <summary>
    /// Gets the importance of the latest email
    /// </summary>
    public MailImportance Importance => latestMailViewModel?.Importance ?? MailImportance.Normal;

    /// <summary>
    /// Gets the thread ID from the latest email
    /// </summary>
    public string ThreadId => latestMailViewModel?.ThreadId ?? _threadId;

    /// <summary>
    /// Gets the message ID from the latest email
    /// </summary>
    public string MessageId => latestMailViewModel?.MessageId ?? string.Empty;

    /// <summary>
    /// Gets the references from the latest email
    /// </summary>
    public string References => latestMailViewModel?.References ?? string.Empty;

    /// <summary>
    /// Gets the in-reply-to from the latest email
    /// </summary>
    public string InReplyTo => latestMailViewModel?.InReplyTo ?? string.Empty;

    /// <summary>
    /// Gets the file ID from the latest email
    /// </summary>
    public Guid FileId => latestMailViewModel?.FileId ?? Guid.Empty;

    /// <summary>
    /// Gets the folder ID from the latest email
    /// </summary>
    public Guid FolderId => latestMailViewModel?.FolderId ?? Guid.Empty;

    /// <summary>
    /// Gets the unique ID from the latest email
    /// </summary>
    public Guid UniqueId => latestMailViewModel?.UniqueId ?? Guid.Empty;

    public string Base64ContactPicture => latestMailViewModel?.MailCopy?.SenderContact?.Base64ContactPicture ?? string.Empty;

    public bool ThumbnailUpdatedEvent => latestMailViewModel?.ThumbnailUpdatedEvent ?? false;

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
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(IsRead))]
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
    [NotifyPropertyChangedFor(nameof(Base64ContactPicture))]
    public partial ObservableCollection<MailItemViewModel> ThreadEmails { get; set; } = [];

    private MailItemViewModel latestMailViewModel => ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).FirstOrDefault()!;

    public DateTime SortingDate => CreationDate;

    public string SortingName => FromName;

    public ThreadMailItemViewModel(string threadId)
    {
        _threadId = threadId;
    }

    /// <summary>
    /// Adds an email to this thread
    /// </summary>
    public void AddEmail(MailItemViewModel email)
    {
        if (email.MailCopy.ThreadId != _threadId)
            throw new ArgumentException($"Email ThreadId '{email.MailCopy.ThreadId}' does not match expander ThreadId '{_threadId}'");

        // Insert email in sorted order by CreationDate (newest first, oldest last)
        var insertIndex = 0;
        for (int i = 0; i < ThreadEmails.Count; i++)
        {
            if (ThreadEmails[i].MailCopy.CreationDate < email.MailCopy.CreationDate)
            {
                insertIndex = i;
                break;
            }
            insertIndex = i + 1;
        }

        ThreadEmails.Insert(insertIndex, email);
        // Reassign to trigger property change notifications
        ThreadEmails = ThreadEmails;
    }

    /// <summary>
    /// Removes an email from this thread
    /// </summary>
    public void RemoveEmail(MailItemViewModel email)
    {
        if (ThreadEmails.Remove(email))
        {
            // Reassign to trigger property change notifications
            ThreadEmails = ThreadEmails;
        }
    }

    /// <summary>
    /// Notifies that a mail item within this thread has been updated.
    /// This raises PropertyChanged for all thread-level computed properties that depend on child items.
    /// </summary>
    /// <param name="updatedMailItem">The mail item that was updated (can be null to refresh all).</param>
    public void NotifyMailItemUpdated(MailItemViewModel updatedMailItem)
    {
        // Raise PropertyChanged for all computed properties that depend on ThreadEmails contents
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(FromName));
        OnPropertyChanged(nameof(CreationDate));
        OnPropertyChanged(nameof(FromAddress));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(IsFlagged));
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(IsRead));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(DraftId));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Importance));
        OnPropertyChanged(nameof(ThreadId));
        OnPropertyChanged(nameof(MessageId));
        OnPropertyChanged(nameof(References));
        OnPropertyChanged(nameof(InReplyTo));
        OnPropertyChanged(nameof(FileId));
        OnPropertyChanged(nameof(FolderId));
        OnPropertyChanged(nameof(UniqueId));
        OnPropertyChanged(nameof(Base64ContactPicture));
        OnPropertyChanged(nameof(ThumbnailUpdatedEvent));
        OnPropertyChanged(nameof(SortingDate));
        OnPropertyChanged(nameof(SortingName));
    }

    /// <summary>
    /// Checks if this thread contains an email with the specified unique ID
    /// </summary>
    public bool HasUniqueId(Guid uniqueId) => ThreadEmails.Any(email => email.MailCopy.UniqueId == uniqueId);

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
