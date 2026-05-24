using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.SyncHost;
using Wino.Messaging.UI;

namespace Wino.SyncHost;

internal sealed class SyncHostEventForwarder
{
    private readonly SyncHostEventPublisher _publisher;
    private bool _isStarted;

    public SyncHostEventForwarder(SyncHostEventPublisher publisher)
    {
        _publisher = publisher;
    }

    public void Start()
    {
        if (_isStarted)
            return;

        Register<AccountSynchronizationProgressUpdatedMessage>(SyncHostProtocol.Events.AccountSynchronizationProgressUpdated);
        Register<AccountSynchronizationCompleted>(SyncHostProtocol.Events.AccountSynchronizationCompleted);
        Register<AccountSynchronizerStateChanged>(SyncHostProtocol.Events.AccountSynchronizerStateChanged);
        Register<SynchronizationActionsAdded>(SyncHostProtocol.Events.SynchronizationActionsAdded);
        Register<SynchronizationActionsCompleted>(SyncHostProtocol.Events.SynchronizationActionsCompleted);
        Register<RefreshUnreadCountsMessage>(SyncHostProtocol.Events.RefreshUnreadCounts);
        Register<AccountFolderConfigurationUpdated>(SyncHostProtocol.Events.AccountFolderConfigurationUpdated);
        Register<AccountCacheResetMessage>(SyncHostProtocol.Events.AccountCacheReset);
        Register<MailAddedMessage>(SyncHostProtocol.Events.MailAdded);
        Register<BulkMailAddedMessage>(SyncHostProtocol.Events.BulkMailAdded);
        Register<MailRemovedMessage>(SyncHostProtocol.Events.MailRemoved);
        Register<BulkMailRemovedMessage>(SyncHostProtocol.Events.BulkMailRemoved);
        Register<MailUpdatedMessage>(SyncHostProtocol.Events.MailUpdated);
        Register<BulkMailUpdatedMessage>(SyncHostProtocol.Events.BulkMailUpdated);
        Register<MailStateUpdatedMessage>(SyncHostProtocol.Events.MailStateUpdated);
        Register<BulkMailStateUpdatedMessage>(SyncHostProtocol.Events.BulkMailStateUpdated);
        Register<MailDownloadedMessage>(SyncHostProtocol.Events.MailDownloaded);
        Register<DraftCreated>(SyncHostProtocol.Events.DraftCreated);
        Register<DraftFailed>(SyncHostProtocol.Events.DraftFailed);
        Register<DraftMapped>(SyncHostProtocol.Events.DraftMapped);
        Register<FolderRenamed>(SyncHostProtocol.Events.FolderRenamed);
        Register<FolderDeleted>(SyncHostProtocol.Events.FolderDeleted);
        Register<FolderSynchronizationEnabled>(SyncHostProtocol.Events.FolderSynchronizationEnabled);
        Register<CalendarItemAdded>(SyncHostProtocol.Events.CalendarItemAdded);
        Register<CalendarItemUpdated>(SyncHostProtocol.Events.CalendarItemUpdated);
        Register<CalendarItemDeleted>(SyncHostProtocol.Events.CalendarItemDeleted);
        Register<CalendarListAdded>(SyncHostProtocol.Events.CalendarListAdded);
        Register<CalendarListUpdated>(SyncHostProtocol.Events.CalendarListUpdated);
        Register<CalendarListDeleted>(SyncHostProtocol.Events.CalendarListDeleted);
        Register<AccountCreatedMessage>(SyncHostProtocol.Events.AccountCreated);
        Register<AccountUpdatedMessage>(SyncHostProtocol.Events.AccountUpdated);
        Register<AccountRemovedMessage>(SyncHostProtocol.Events.AccountRemoved);

        _isStarted = true;
    }

    private void Register<TMessage>(string eventType)
        where TMessage : class
        => WeakReferenceMessenger.Default.Register<TMessage>(this, (_, message) =>
        {
            _ = _publisher.PublishAsync(eventType, message);
        });
}
