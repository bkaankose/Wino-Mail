using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.ViewModels;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public class MailBaseViewModel : CoreBaseViewModel,
    IRecipient<MailAddedMessage>,
    IRecipient<MailRemovedMessage>,
    IRecipient<MailUpdatedMessage>,
    IRecipient<MailDownloadedMessage>,
    IRecipient<DraftCreated>,
    IRecipient<DraftFailed>,
    IRecipient<DraftMapped>,
    IRecipient<FolderRenamed>,
    IRecipient<FolderSynchronizationEnabled>
{
    protected virtual void OnMailAdded(MailCopy addedMail) { }
    protected virtual void OnMailRemoved(MailCopy removedMail) { }
    protected virtual void OnMailUpdated(MailCopy updatedMail, MailUpdateSource source) { }
    protected virtual void OnMailDownloaded(MailCopy downloadedMail) { }
    protected virtual void OnDraftCreated(MailCopy draftMail, MailAccount account) { }
    protected virtual void OnDraftFailed(MailCopy draftMail, MailAccount account) { }
    protected virtual void OnDraftMapped(string localDraftCopyId, string remoteDraftCopyId) { }
    protected virtual void OnFolderRenamed(IMailItemFolder mailItemFolder) { }
    protected virtual void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder) { }

    void IRecipient<MailAddedMessage>.Receive(MailAddedMessage message) => OnMailAdded(message.AddedMail);
    void IRecipient<MailRemovedMessage>.Receive(MailRemovedMessage message) => OnMailRemoved(message.RemovedMail);
    void IRecipient<MailUpdatedMessage>.Receive(MailUpdatedMessage message) => OnMailUpdated(message.UpdatedMail, message.Source);
    void IRecipient<MailDownloadedMessage>.Receive(MailDownloadedMessage message) => OnMailDownloaded(message.DownloadedMail);

    void IRecipient<DraftMapped>.Receive(DraftMapped message) => OnDraftMapped(message.LocalDraftCopyId, message.RemoteDraftCopyId);
    void IRecipient<DraftFailed>.Receive(DraftFailed message) => OnDraftFailed(message.DraftMail, message.Account);
    void IRecipient<DraftCreated>.Receive(DraftCreated message) => OnDraftCreated(message.DraftMail, message.Account);

    void IRecipient<FolderRenamed>.Receive(FolderRenamed message) => OnFolderRenamed(message.MailItemFolder);
    void IRecipient<FolderSynchronizationEnabled>.Receive(FolderSynchronizationEnabled message) => OnFolderSynchronizationEnabled(message.MailItemFolder);

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        UnregisterRecipients();

        Messenger.Register<MailAddedMessage>(this);
        Messenger.Register<MailRemovedMessage>(this);
        Messenger.Register<MailUpdatedMessage>(this);
        Messenger.Register<MailDownloadedMessage>(this);
        Messenger.Register<DraftCreated>(this);
        Messenger.Register<DraftFailed>(this);
        Messenger.Register<DraftMapped>(this);
        Messenger.Register<FolderRenamed>(this);
        Messenger.Register<FolderSynchronizationEnabled>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<MailAddedMessage>(this);
        Messenger.Unregister<MailRemovedMessage>(this);
        Messenger.Unregister<MailUpdatedMessage>(this);
        Messenger.Unregister<MailDownloadedMessage>(this);
        Messenger.Unregister<DraftCreated>(this);
        Messenger.Unregister<DraftFailed>(this);
        Messenger.Unregister<DraftMapped>(this);
        Messenger.Unregister<FolderRenamed>(this);
        Messenger.Unregister<FolderSynchronizationEnabled>(this);
    }
}
