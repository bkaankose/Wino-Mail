using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Authentication;
using MailCal.Core.Domain.Models.Connectivity;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Interface for the singleton synchronization manager that handles synchronizer instances and operations.
/// </summary>
public interface ISynchronizationManager
{
    Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the SynchronizationManager with required dependencies.
    /// </summary>
    Task InitializeAsync(ISynchronizerFactory synchronizerFactory,
                        IMailServerTestService mailServerTestService,
                        IAccountService accountService,
                        INotificationBuilder notificationBuilder,
                        IAuthenticationProvider authenticationProvider,
                        IPreferencesService preferencesService,
                        IDraftSyncRetryService draftSyncRetryService,
                        IDraftUpdateCoordinator draftUpdateCoordinator = null, IMailService draftMailService = null);

    /// <summary>
    /// Tests IMAP server connectivity for the given server information.
    /// </summary>
    Task<ImapConnectivityTestResults> TestImapConnectivityAsync(CustomServerInformation serverInformation);

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
    Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization);

    /// <summary>
    /// Queues a grouped action that may contain requests for multiple accounts.
    /// </summary>
    Task QueueRequestPackAsync(IReadOnlyDictionary<Guid, List<IRequestBase>> requestsByAccount, bool triggerSynchronization);

    /// <summary>
    /// Cancels the latest undoable queued action for the given account.
    /// </summary>
    Task UndoLatestQueuedAction(Guid accountId);

    /// <summary>
    /// Cancels the latest undoable queued action for the given synchronizer account.
    /// </summary>
    Task UndoLatestQueuedAction(IMailCalSynchronizerBase synchronizer);

    /// <summary>
    /// Checks whether a delete request for a mail is waiting in the undo queue.
    /// </summary>
    bool IsDeleteRequestQueued(Guid accountId, Guid uniqueMailId);

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

    Task<ContactSynchronizationResult> SynchronizeContactsAsync(ContactSynchronizationOptions options,
                                                                 CancellationToken cancellationToken = default);

    Task<TaskSynchronizationResult> SynchronizeTasksAsync(TaskSynchronizationOptions options,
                                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a MIME message for the given mail item.
    /// </summary>
    Task<string> DownloadMimeMessageAsync(MailCopy mailItem, Guid accountId,
                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new synchronizer for a newly added account.
    /// </summary>
    IMailCalSynchronizerBase CreateSynchronizerForAccount(MailAccount account);

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
    IEnumerable<IMailCalSynchronizerBase> GetAllSynchronizers();

    /// <summary>
    /// Gets a synchronizer for the given account ID.
    /// </summary>
    Task<IMailCalSynchronizerBase> GetSynchronizerAsync(Guid accountId);

    /// <summary>
    /// Handles OAuth authentication for the specified provider.
    /// </summary>
    Task<TokenInformationEx> HandleAuthorizationAsync(MailProviderType providerType,
                                                     MailAccount account = null,
                                                     bool forceInteractive = false,
                                                     IReadOnlyCollection<ProviderFeature> requestedFeatures = null);
}
