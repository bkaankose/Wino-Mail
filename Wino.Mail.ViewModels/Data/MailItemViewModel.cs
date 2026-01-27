using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Single view model for IMailItem representation.
/// </summary>
public partial class MailItemViewModel(MailCopy mailCopy) : ObservableRecipient, IMailListItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreationDate))]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
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
    [NotifyPropertyChangedFor(nameof(Importance))]
    [NotifyPropertyChangedFor(nameof(ThreadId))]
    [NotifyPropertyChangedFor(nameof(MessageId))]
    [NotifyPropertyChangedFor(nameof(References))]
    [NotifyPropertyChangedFor(nameof(InReplyTo))]
    [NotifyPropertyChangedFor(nameof(FileId))]
    [NotifyPropertyChangedFor(nameof(FolderId))]
    [NotifyPropertyChangedFor(nameof(UniqueId))]
    [NotifyPropertyChangedFor(nameof(Base64ContactPicture))]
    public partial MailCopy MailCopy { get; set; } = mailCopy;

    [ObservableProperty]
    public partial bool IsDisplayedInThread { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial bool IsSelected { get; set; }

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

    public string Base64ContactPicture
    {
        get => MailCopy.SenderContact?.Base64ContactPicture ?? string.Empty;
        set => SetProperty(MailCopy.SenderContact.Base64ContactPicture, value, MailCopy, (u, n) => u.SenderContact.Base64ContactPicture = n);
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

    /// <summary>
    /// Updates the MailCopy with new data and notifies all bound properties.
    /// This method copies values from the source to the existing MailCopy to maintain reference integrity,
    /// then explicitly raises PropertyChanged for all dependent properties.
    /// </summary>
    /// <param name="source">The source MailCopy with updated values.</param>
    public void UpdateFrom(MailCopy source)
    {
        if (source == null) return;

        // Update the underlying MailCopy properties directly to maintain reference integrity
        // This is important because other parts of the app may hold references to this MailCopy
        // Note: UniqueId is the primary key and should match - we don't update it
        MailCopy.Id = source.Id;
        MailCopy.FolderId = source.FolderId;
        MailCopy.ThreadId = source.ThreadId;
        MailCopy.MessageId = source.MessageId;
        MailCopy.References = source.References;
        MailCopy.InReplyTo = source.InReplyTo;
        MailCopy.IsDraft = source.IsDraft;
        MailCopy.DraftId = source.DraftId;
        MailCopy.CreationDate = source.CreationDate;
        MailCopy.Subject = source.Subject;
        MailCopy.PreviewText = source.PreviewText;
        MailCopy.FromName = source.FromName;
        MailCopy.FromAddress = source.FromAddress;
        MailCopy.HasAttachments = source.HasAttachments;
        MailCopy.Importance = source.Importance;
        MailCopy.IsRead = source.IsRead;
        MailCopy.IsFlagged = source.IsFlagged;
        MailCopy.IsFocused = source.IsFocused;
        MailCopy.FileId = source.FileId;
        MailCopy.ItemType = source.ItemType;
        MailCopy.SenderContact = source.SenderContact;
        MailCopy.AssignedAccount = source.AssignedAccount;
        MailCopy.AssignedFolder = source.AssignedFolder;

        // Raise PropertyChanged for all properties that XAML may bind to
        OnPropertyChanged(nameof(CreationDate));
        OnPropertyChanged(nameof(IsFlagged));
        OnPropertyChanged(nameof(FromName));
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(IsRead));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(DraftId));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(FromAddress));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(Importance));
        OnPropertyChanged(nameof(ThreadId));
        OnPropertyChanged(nameof(MessageId));
        OnPropertyChanged(nameof(References));
        OnPropertyChanged(nameof(InReplyTo));
        OnPropertyChanged(nameof(FileId));
        OnPropertyChanged(nameof(FolderId));
        OnPropertyChanged(nameof(UniqueId));
        OnPropertyChanged(nameof(Base64ContactPicture));
        OnPropertyChanged(nameof(SortingDate));
        OnPropertyChanged(nameof(SortingName));
    }
}
