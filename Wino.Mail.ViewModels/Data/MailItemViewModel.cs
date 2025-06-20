using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Single view model for IMailItem representation.
/// </summary>
public partial class MailItemViewModel(MailCopy mailCopy) : ObservableObject, IMailItem
{
    [ObservableProperty]
    public partial MailCopy MailCopy { get; set; } = mailCopy;

    public Guid UniqueId => ((IMailItem)MailCopy).UniqueId;
    public string ThreadId => ((IMailItem)MailCopy).ThreadId;
    public string MessageId => ((IMailItem)MailCopy).MessageId;
    public DateTime CreationDate => ((IMailItem)MailCopy).CreationDate;
    public string References => ((IMailItem)MailCopy).References;
    public string InReplyTo => ((IMailItem)MailCopy).InReplyTo;

    [ObservableProperty]
    public partial bool ThumbnailUpdatedEvent { get; set; } = false;

    [ObservableProperty]
    public partial bool IsCustomFocused { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

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

    public MailItemFolder AssignedFolder => ((IMailItem)MailCopy).AssignedFolder;

    public MailAccount AssignedAccount => ((IMailItem)MailCopy).AssignedAccount;

    public Guid FileId => ((IMailItem)MailCopy).FileId;

    public AccountContact SenderContact => ((IMailItem)MailCopy).SenderContact;

    public IEnumerable<Guid> GetContainingIds() => new[] { UniqueId };
}
