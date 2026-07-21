using System.Text.Json.Serialization;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Messaging;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MailAddedMessage))]
[JsonSerializable(typeof(MailDownloadedMessage))]
[JsonSerializable(typeof(MailRemovedMessage))]
[JsonSerializable(typeof(MailUpdatedMessage))]
[JsonSerializable(typeof(BulkMailAddedMessage))]
[JsonSerializable(typeof(BulkMailRemovedMessage))]
[JsonSerializable(typeof(BulkMailUpdatedMessage))]
[JsonSerializable(typeof(MailStateUpdatedMessage))]
[JsonSerializable(typeof(BulkMailStateUpdatedMessage))]
[JsonSerializable(typeof(AccountCreatedMessage))]
[JsonSerializable(typeof(AccountRemovedMessage))]
[JsonSerializable(typeof(AccountUpdatedMessage))]
[JsonSerializable(typeof(DraftCreated))]
[JsonSerializable(typeof(DraftFailed))]
[JsonSerializable(typeof(DraftMapped))]
[JsonSerializable(typeof(FolderDeleted))]
[JsonSerializable(typeof(FolderRenamed))]
[JsonSerializable(typeof(FolderSynchronizationEnabled))]
[JsonSerializable(typeof(MergedInboxRenamed))]
[JsonSerializable(typeof(AccountSynchronizationCompleted))]
[JsonSerializable(typeof(AccountCalendarSynchronizationStateChanged))]
[JsonSerializable(typeof(RefreshUnreadCountsMessage))]
[JsonSerializable(typeof(AccountSynchronizerStateChanged))]
[JsonSerializable(typeof(AccountSynchronizationProgress))]
[JsonSerializable(typeof(AccountSynchronizationProgressUpdatedMessage))]
[JsonSerializable(typeof(AccountFolderConfigurationUpdated))]
[JsonSerializable(typeof(SynchronizationActionsCompleted))]
[JsonSerializable(typeof(UndoableMailActionPackChanged))]
[JsonSerializable(typeof(CopyAuthURLRequested))]
[JsonSerializable(typeof(WinoAccountAddOnPurchasedMessage))]
[JsonSerializable(typeof(WinoAccountProfileDeletedMessage))]
[JsonSerializable(typeof(WinoAccountProfileUpdatedMessage))]
[JsonSerializable(typeof(CalendarListAdded))]
[JsonSerializable(typeof(CalendarListUpdated))]
[JsonSerializable(typeof(CalendarListDeleted))]
[JsonSerializable(typeof(CalendarItemAdded))]
[JsonSerializable(typeof(CalendarItemUpdated))]
[JsonSerializable(typeof(CalendarItemDeleted))]
[JsonSerializable(typeof(AccountMenuItemsReordered))]
[JsonSerializable(typeof(AccountsMenuRefreshRequested))]
[JsonSerializable(typeof(NewMailSynchronizationRequested))]
[JsonSerializable(typeof(NewCalendarSynchronizationRequested))]
[JsonSerializable(typeof(KillAccountSynchronizerRequested))]
[JsonSerializable(typeof(AccountCacheResetMessage))]
public partial class CommunicationMessagesContext : JsonSerializerContext;
