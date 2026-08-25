using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Messaging;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MailAddedMessage))]
[JsonSerializable(typeof(MailDownloadedMessage))]
[JsonSerializable(typeof(MailRemovedMessage))]
[JsonSerializable(typeof(MailUpdatedMessage))]
[JsonSerializable(typeof(AccountCreatedMessage))]
[JsonSerializable(typeof(AccountRemovedMessage))]
[JsonSerializable(typeof(AccountUpdatedMessage))]
[JsonSerializable(typeof(DraftCreated))]
[JsonSerializable(typeof(DraftFailed))]
[JsonSerializable(typeof(DraftMapped))]
[JsonSerializable(typeof(FolderRenamed))]
[JsonSerializable(typeof(FolderSynchronizationEnabled))]
[JsonSerializable(typeof(MergedInboxRenamed))]
[JsonSerializable(typeof(AccountSynchronizationCompleted))]
[JsonSerializable(typeof(RefreshUnreadCountsMessage))]
[JsonSerializable(typeof(AccountSynchronizerStateChanged))]
[JsonSerializable(typeof(AccountSynchronizationProgress))]
[JsonSerializable(typeof(AccountSynchronizationProgressUpdatedMessage))]
[JsonSerializable(typeof(AccountFolderConfigurationUpdated))]
[JsonSerializable(typeof(CopyAuthURLRequested))]
[JsonSerializable(typeof(NewMailSynchronizationRequested))]
[JsonSerializable(typeof(NewContactSynchronizationRequested))]
[JsonSerializable(typeof(ContactSynchronizationOptions))]
[JsonSerializable(typeof(ContactSynchronizationCompleted))]
[JsonSerializable(typeof(NewTaskSynchronizationRequested))]
[JsonSerializable(typeof(TaskSynchronizationOptions))]
[JsonSerializable(typeof(TaskSynchronizationCompleted))]
[JsonSerializable(typeof(AccountCacheResetMessage))]
public partial class CommunicationMessagesContext : JsonSerializerContext;
