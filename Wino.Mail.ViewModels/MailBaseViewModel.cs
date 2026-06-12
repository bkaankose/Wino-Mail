using CommunityToolkit.Mvvm.Messaging;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.ViewModels;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public class MailBaseViewModel : CoreBaseViewModel,
    IRecipient<MailAddedMessage>,
    IRecipient<BulkMailAddedMessage>,
    IRecipient<MailRemovedMessage>,
    IRecipient<BulkMailRemovedMessage>,
    IRecipient<MailStateUpdatedMessage>,
    IRecipient<BulkMailStateUpdatedMessage>,
    IRecipient<MailUpdatedMessage>,
    IRecipient<BulkMailUpdatedMessage>,
    IRecipient<MailDownloadedMessage>,
    IRecipient<DraftCreated>,
    IRecipient<DraftFailed>,
    IRecipient<DraftMapped>,
    IRecipient<FolderRenamed>,
    IRecipient<FolderDeleted>,
    IRecipient<FolderSynchronizationEnabled>
{
    protected virtual void OnMailAdded(MailCopy addedMail, EntityUpdateSource source) { }
    protected virtual void OnBulkMailAdded(IReadOnlyList<MailCopy> addedMails, EntityUpdateSource source)
    {
        foreach (var addedMail in addedMails ?? [])
        {
            OnMailAdded(addedMail, source);
        }
    }

    protected virtual void OnMailRemoved(MailCopy removedMail, EntityUpdateSource source) { }
    protected virtual void OnBulkMailRemoved(IReadOnlyList<MailCopy> removedMails, EntityUpdateSource source)
    {
        foreach (var removedMail in removedMails ?? [])
        {
            OnMailRemoved(removedMail, source);
        }
    }

    protected virtual void OnMailStateUpdated(MailStateChange updatedState, EntityUpdateSource source) { }
    protected virtual void OnBulkMailStateUpdated(IReadOnlyList<MailStateChange> updatedStates, EntityUpdateSource source)
    {
        foreach (var updatedState in updatedStates ?? [])
        {
            OnMailStateUpdated(updatedState, source);
        }
    }

    protected virtual void OnMailUpdated(MailCopy updatedMail, EntityUpdateSource source, MailCopyChangeFlags changedProperties) { }
    protected virtual void OnBulkMailUpdated(IReadOnlyList<MailCopy> updatedMails, EntityUpdateSource source, MailCopyChangeFlags changedProperties)
    {
        foreach (var updatedMail in updatedMails ?? [])
        {
            OnMailUpdated(updatedMail, source, changedProperties);
        }
    }

    protected virtual void OnMailDownloaded(MailCopy downloadedMail) { }
    protected virtual void OnDraftCreated(MailCopy draftMail, MailAccount account) { }
    protected virtual void OnDraftFailed(MailCopy draftMail, MailAccount account) { }
    protected virtual void OnDraftMapped(string localDraftCopyId, string remoteDraftCopyId) { }
    protected virtual void OnFolderRenamed(IMailItemFolder mailItemFolder) { }
    protected virtual void OnFolderDeleted(MailItemFolder folder) { }
    protected virtual void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder) { }

    void IRecipient<MailAddedMessage>.Receive(MailAddedMessage message) => OnMailAdded(message.AddedMail, message.Source);
    void IRecipient<BulkMailAddedMessage>.Receive(BulkMailAddedMessage message) => OnBulkMailAdded(message.AddedMails, message.Source);
    void IRecipient<MailRemovedMessage>.Receive(MailRemovedMessage message) => OnMailRemoved(message.RemovedMail, message.Source);
    void IRecipient<BulkMailRemovedMessage>.Receive(BulkMailRemovedMessage message) => OnBulkMailRemoved(message.RemovedMails, message.Source);
    void IRecipient<MailStateUpdatedMessage>.Receive(MailStateUpdatedMessage message) => OnMailStateUpdated(message.UpdatedState, message.Source);
    void IRecipient<BulkMailStateUpdatedMessage>.Receive(BulkMailStateUpdatedMessage message) => OnBulkMailStateUpdated(message.UpdatedStates, message.Source);
    void IRecipient<MailUpdatedMessage>.Receive(MailUpdatedMessage message) => OnMailUpdated(message.UpdatedMail, message.Source, message.ChangedProperties);
    void IRecipient<BulkMailUpdatedMessage>.Receive(BulkMailUpdatedMessage message) => OnBulkMailUpdated(message.UpdatedMails, message.Source, message.ChangedProperties);
    void IRecipient<MailDownloadedMessage>.Receive(MailDownloadedMessage message) => OnMailDownloaded(message.DownloadedMail);

    void IRecipient<DraftMapped>.Receive(DraftMapped message) => OnDraftMapped(message.LocalDraftCopyId, message.RemoteDraftCopyId);
    void IRecipient<DraftFailed>.Receive(DraftFailed message) => OnDraftFailed(message.DraftMail, message.Account);
    void IRecipient<DraftCreated>.Receive(DraftCreated message) => OnDraftCreated(message.DraftMail, message.Account);

    void IRecipient<FolderRenamed>.Receive(FolderRenamed message) => OnFolderRenamed(message.MailItemFolder);
    void IRecipient<FolderDeleted>.Receive(FolderDeleted message) => OnFolderDeleted(message.MailItemFolder);
    void IRecipient<FolderSynchronizationEnabled>.Receive(FolderSynchronizationEnabled message) => OnFolderSynchronizationEnabled(message.MailItemFolder);

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        UnregisterRecipients();

        Messenger.Register<MailAddedMessage>(this);
        Messenger.Register<BulkMailAddedMessage>(this);
        Messenger.Register<MailRemovedMessage>(this);
        Messenger.Register<BulkMailRemovedMessage>(this);
        Messenger.Register<MailStateUpdatedMessage>(this);
        Messenger.Register<BulkMailStateUpdatedMessage>(this);
        Messenger.Register<MailUpdatedMessage>(this);
        Messenger.Register<BulkMailUpdatedMessage>(this);
        Messenger.Register<MailDownloadedMessage>(this);
        Messenger.Register<DraftCreated>(this);
        Messenger.Register<DraftFailed>(this);
        Messenger.Register<DraftMapped>(this);
        Messenger.Register<FolderRenamed>(this);
        Messenger.Register<FolderDeleted>(this);
        Messenger.Register<FolderSynchronizationEnabled>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<MailAddedMessage>(this);
        Messenger.Unregister<BulkMailAddedMessage>(this);
        Messenger.Unregister<MailRemovedMessage>(this);
        Messenger.Unregister<BulkMailRemovedMessage>(this);
        Messenger.Unregister<MailStateUpdatedMessage>(this);
        Messenger.Unregister<BulkMailStateUpdatedMessage>(this);
        Messenger.Unregister<MailUpdatedMessage>(this);
        Messenger.Unregister<BulkMailUpdatedMessage>(this);
        Messenger.Unregister<MailDownloadedMessage>(this);
        Messenger.Unregister<DraftCreated>(this);
        Messenger.Unregister<DraftFailed>(this);
        Messenger.Unregister<DraftMapped>(this);
        Messenger.Unregister<FolderRenamed>(this);
        Messenger.Unregister<FolderDeleted>(this);
        Messenger.Unregister<FolderSynchronizationEnabled>(this);
    }
}
