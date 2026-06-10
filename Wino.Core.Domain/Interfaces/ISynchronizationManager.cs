using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Interface for the singleton synchronization manager that handles synchronizer instances and operations.
/// </summary>
[Wino.Core.Domain.Attributes.WinoRpcService]
public interface ISynchronizationManager
{
    /// <summary>
    /// Initializes the SynchronizationManager with required dependencies.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task InitializeAsync(ISynchronizerFactory synchronizerFactory,
                        IImapTestService imapTestService,
                        IAccountService accountService,
                        INotificationBuilder notificationBuilder,
                        IAuthenticationProvider authenticationProvider,
                        IWinoTelemetryService telemetryService,
                        IPreferencesService preferencesService);

    /// <summary>
    /// Tests IMAP server connectivity for the given server information.
    /// </summary>
    Task<ImapConnectivityTestResults> TestImapConnectivityAsync(CustomServerInformation serverInformation, bool allowSSLHandshake);

    /// <summary>
    /// Starts a new mail synchronization for the given account.
    /// </summary>
    Task<MailSynchronizationResult> SynchronizeMailAsync(MailSynchronizationOptions options,
                                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if there is an ongoing synchronization for the given account.
    /// </summary>
    bool IsAccountSynchronizing(Guid accountId);

    /// <summary>
    /// Gets the latest centralized synchronization progress snapshot for the given account and category.
    /// </summary>
    AccountSynchronizationProgress GetSynchronizationProgress(Guid accountId, SynchronizationProgressCategory category);

    /// <summary>
    /// Queues a mail action request to the corresponding account's synchronizer with optional synchronization triggering.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization);

    /// <summary>
    /// Queues a grouped action that may contain requests for multiple accounts.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task QueueRequestPackAsync(IReadOnlyDictionary<Guid, List<IRequestBase>> requestsByAccount, bool triggerSynchronization);

    /// <summary>
    /// Cancels the latest undoable queued action for the given account.
    /// </summary>
    Task UndoLatestQueuedAction(Guid accountId);

    /// <summary>
    /// Cancels the latest undoable queued action for the given synchronizer account.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task UndoLatestQueuedAction(IWinoSynchronizerBase synchronizer);

    /// <summary>
    /// Handles folder synchronization for the given account.
    /// </summary>
    Task<MailSynchronizationResult> SynchronizeFoldersAsync(Guid accountId,
                                                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles alias synchronization for the given account.
    /// </summary>
    Task<MailSynchronizationResult> SynchronizeAliasesAsync(Guid accountId,
                                                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles category synchronization for the given account.
    /// </summary>
    Task<MailSynchronizationResult> SynchronizeCategoriesAsync(Guid accountId,
                                                               CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles profile synchronization for the given account.
    /// </summary>
    Task<MailSynchronizationResult> SynchronizeProfileAsync(Guid accountId,
                                                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles calendar synchronization for the given account.
    /// </summary>
    Task<CalendarSynchronizationResult> SynchronizeCalendarAsync(CalendarSynchronizationOptions options,
                                                                  CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a MIME message for the given mail item.
    /// </summary>
    Task<string> DownloadMimeMessageAsync(MailCopy mailItem, Guid accountId,
                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new synchronizer for a newly added account.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    IWinoSynchronizerBase CreateSynchronizerForAccount(MailAccount account);

    /// <summary>
    /// Cancels ongoing synchronizations for the given account.
    /// </summary>
    Task CancelSynchronizationsAsync(Guid accountId);

    /// <summary>
    /// Destroys the synchronizer for the given account.
    /// </summary>
    Task DestroySynchronizerAsync(Guid accountId);

    /// <summary>
    /// Gets all cached synchronizers.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    IEnumerable<IWinoSynchronizerBase> GetAllSynchronizers();

    /// <summary>
    /// Gets a synchronizer for the given account ID.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task<IWinoSynchronizerBase> GetSynchronizerAsync(Guid accountId);

    /// <summary>
    /// Handles OAuth authentication for the specified provider, including interactive
    /// flows. The WAM broker dialog is parented to <paramref name="parentWindowHandle"/>
    /// (the UI window; the broker runs out-of-process so a cross-process handle works),
    /// and the Gmail flow launches the system browser with a loopback listener, which
    /// needs no window. Tokens land in the shared caches in the publisher folder.
    /// </summary>
    Task<TokenInformationEx> HandleAuthorizationAsync(MailProviderType providerType,
                                                     MailAccount account = null,
                                                     bool proposeCopyAuthorizationURL = false,
                                                     bool forceInteractive = false,
                                                     long parentWindowHandle = 0);

    /// <summary>
    /// Returns unique mail ids that currently have queued (pending) operations.
    /// Used by the UI to show busy indicators on mail items.
    /// </summary>
    Task<List<Guid>> GetPendingMailOperationUniqueIdsAsync(Guid accountId);

    /// <summary>
    /// Checks whether the given mail item has a queued (pending) operation.
    /// </summary>
    Task<bool> HasPendingMailOperationAsync(Guid accountId, Guid mailUniqueId);

    /// <summary>
    /// Returns calendar item ids that currently have queued (pending) operations.
    /// </summary>
    Task<List<Guid>> GetPendingCalendarOperationIdsAsync(Guid accountId);

    /// <summary>
    /// Performs a provider online search over the given folders.
    /// </summary>
    Task<List<MailCopy>> OnlineSearchAsync(Guid accountId, string queryText, List<MailItemFolder> folders, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a calendar attachment to the given local file path in the shared storage.
    /// </summary>
    Task DownloadCalendarAttachmentAsync(Guid accountId,
                                         Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
                                         Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
                                         string localFilePath,
                                         CancellationToken cancellationToken = default);
}
