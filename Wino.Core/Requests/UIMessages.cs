using System;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record MailAddedMessage(MailCopy AddedMail) : IUIMessage;
    public record MailRemovedMessage(MailCopy RemovedMail) : IUIMessage;
    public record MailUpdatedMessage(MailCopy UpdatedMail) : IUIMessage;
    public record MailDownloadedMessage(MailCopy DownloadedMail) : IUIMessage;

    public record FolderAddedMessage(MailItemFolder AddedFolder, MailAccount Account) : IUIMessage;
    public record FolderRemovedMessage(MailItemFolder RemovedFolder, MailAccount Account) : IUIMessage;
    public record FolderUpdatedMessage(MailItemFolder UpdatedFolder, MailAccount Account) : IUIMessage;

    public record AccountCreatedMessage(MailAccount Account) : IUIMessage;
    public record AccountRemovedMessage(MailAccount Account) : IUIMessage;
    public record AccountUpdatedMessage(MailAccount Account) : IUIMessage;

    public record DraftCreated(MailCopy DraftMail, MailAccount Account) : IUIMessage;
    public record DraftFailed(MailCopy DraftMail, MailAccount Account) : IUIMessage;
    public record DraftMapped(string LocalDraftCopyId, string RemoteDraftCopyId) : IUIMessage;

    public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : IUIMessage;
}
