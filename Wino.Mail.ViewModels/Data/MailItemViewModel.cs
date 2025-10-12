using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Single view model for IMailItem representation.
/// </summary>
public partial class MailItemViewModel(MailCopy mailCopy) : ObservableObject
{
    [ObservableProperty]
    public partial MailCopy MailCopy { get; set; } = mailCopy;

    [ObservableProperty]
    public partial bool ThumbnailUpdatedEvent { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsDisplayedInThread { get; set; }

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
}
