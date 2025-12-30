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
public interface ISynchronizationManager
{
    /// <summary>
    /// Initializes the SynchronizationManager with required dependencies.
    /// </summary>
    Task InitializeAsync(ISynchronizerFactory synchronizerFactory,
                        IImapTestService imapTestService,
                        IAccountService accountService,
                        INotificationBuilder notificationBuilder,
                        IAuthenticationProvider authenticationProvider);

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
    /// Queues a mail action request to the corresponding account's synchronizer with optional synchronization triggering.
    /// </summary>
    Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization);

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
    IWinoSynchronizerBase CreateSynchronizerForAccount(MailAccount account);

    /// <summary>
    /// Destroys the synchronizer for the given account.
    /// </summary>
    Task DestroySynchronizerAsync(Guid accountId);

    /// <summary>
    /// Gets all cached synchronizers.
    /// </summary>
    IEnumerable<IWinoSynchronizerBase> GetAllSynchronizers();

    /// <summary>
    /// Gets a synchronizer for the given account ID.
    /// </summary>
    Task<IWinoSynchronizerBase> GetSynchronizerAsync(Guid accountId);

    /// <summary>
    /// Handles OAuth authentication for the specified provider.
    /// </summary>
    Task<TokenInformationEx> HandleAuthorizationAsync(MailProviderType providerType,
                                                     MailAccount account = null,
                                                     bool proposeCopyAuthorizationURL = false);
}
