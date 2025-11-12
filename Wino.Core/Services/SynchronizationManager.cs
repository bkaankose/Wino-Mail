using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Services;

/// <summary>
/// Singleton manager that handles synchronizer instances and operations for all accounts.
/// Replaces the old WinoServerConnectionManager functionality.
/// </summary>
public class SynchronizationManager : ISynchronizationManager
{
    private static readonly Lazy<SynchronizationManager> _instance = new(() => new SynchronizationManager());
    public static SynchronizationManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<Guid, IWinoSynchronizerBase> _synchronizerCache = new();
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<SynchronizationManager>();

    private SynchronizerFactory _concreteSynchronizerFactory;
    private IImapTestService _imapTestService;
    private IAccountService _accountService;
    private IAuthenticationProvider _authenticationProvider;
    private INotificationBuilder _notificationBuilder;

    private bool _isInitialized = false;

    private SynchronizationManager() { }

    /// <summary>
    /// Initializes the SynchronizationManager with required dependencies.
    /// This must be called before using any other methods.
    /// </summary>
    /// <param name="synchronizerFactory">Factory for creating synchronizers</param>
    /// <param name="imapTestService">Service for testing IMAP connectivity</param>
    /// <param name="accountService">Service for account operations</param>
    /// <param name="authenticationProvider">Provider for OAuth authentication</param>
    public async Task InitializeAsync(ISynchronizerFactory synchronizerFactory,
                                     IImapTestService imapTestService,
                                     IAccountService accountService,
                                     INotificationBuilder notificationBuilder,
                                     IAuthenticationProvider authenticationProvider)
    {
        await _initializationSemaphore.WaitAsync();

        try
        {
            if (_isInitialized) return;

            _concreteSynchronizerFactory = synchronizerFactory as SynchronizerFactory ?? throw new ArgumentException("SynchronizerFactory must be the concrete implementation");
            _imapTestService = imapTestService ?? throw new ArgumentNullException(nameof(imapTestService));
            _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
            _authenticationProvider = authenticationProvider ?? throw new ArgumentNullException(nameof(authenticationProvider));
            _notificationBuilder = notificationBuilder ?? throw new ArgumentNullException(nameof(notificationBuilder));

            // Get all accounts and create synchronizers for them
            var accounts = await _accountService.GetAccountsAsync();

            foreach (var account in accounts)
            {
                try
                {
                    var synchronizer = _concreteSynchronizerFactory.CreateNewSynchronizer(account);
                    _synchronizerCache.TryAdd(account.Id, synchronizer);

                    _logger.Information("Created synchronizer for account {AccountName} ({AccountId})",
                                      account.Name, account.Id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to create synchronizer for account {AccountName} ({AccountId})",
                                account.Name, account.Id);
                }
            }

            _isInitialized = true;
            _logger.Information("SynchronizationManager initialized with {Count} synchronizers", _synchronizerCache.Count);
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    /// <summary>
    /// Tests IMAP server connectivity for the given server information.
    /// </summary>
    /// <param name="serverInformation">Server information to test</param>
    /// <param name="allowSSLHandshake">Whether to allow SSL handshake</param>
    /// <returns>Test results indicating success or failure with details</returns>
    public async Task<ImapConnectivityTestResults> TestImapConnectivityAsync(CustomServerInformation serverInformation, bool allowSSLHandshake)
    {
        EnsureInitialized();

        try
        {
            _logger.Information("Testing IMAP connectivity for {Server}:{Port}",
                              serverInformation.IncomingServer,
                              serverInformation.IncomingServerPort);

            await _imapTestService.TestImapConnectionAsync(serverInformation, allowSSLHandshake);

            _logger.Information("IMAP connectivity test successful");
            return ImapConnectivityTestResults.Success();
        }
        catch (ImapTestSSLCertificateException sslTestException)
        {
            _logger.Warning("IMAP connectivity test requires SSL certificate confirmation");
            return ImapConnectivityTestResults.CertificateUIRequired(
                sslTestException.Issuer,
                sslTestException.ExpirationDateString,
                sslTestException.ValidFromDateString);
        }
        catch (ImapClientPoolException clientPoolException)
        {
            _logger.Error(clientPoolException, "IMAP connectivity test failed with protocol log");
            return ImapConnectivityTestResults.Failure(clientPoolException, clientPoolException.ProtocolLog);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "IMAP connectivity test failed");
            return ImapConnectivityTestResults.Failure(exception, string.Empty);
        }
    }

    /// <summary>
    /// Starts a new mail synchronization for the given account.
    /// </summary>
    /// <param name="options">Mail synchronization options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<MailSynchronizationResult> SynchronizeMailAsync(MailSynchronizationOptions options,
                                                                      CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var synchronizer = await GetOrCreateSynchronizerAsync(options.AccountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId}", options.AccountId);
            return MailSynchronizationResult.Failed;
        }

        _logger.Information("Starting mail synchronization for account {AccountId} with type {SyncType}",
                          options.AccountId, options.Type);

        try
        {
            var result = await synchronizer.SynchronizeMailsAsync(options, cancellationToken);

            _logger.Information("Mail synchronization completed for account {AccountId} with state {State}",
                              options.AccountId, result.CompletedState);

            // Create notifications.
            if (result.DownloadedMessages?.Any() ?? false)
                await _notificationBuilder.CreateNotificationsAsync(result.DownloadedMessages);

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Mail synchronization failed for account {AccountId}", options.AccountId);
            return MailSynchronizationResult.Failed;
        }
    }

    /// <summary>
    /// Checks if there is an ongoing synchronization for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to check</param>
    /// <returns>True if synchronization is ongoing, false otherwise</returns>
    public bool IsAccountSynchronizing(Guid accountId)
    {
        EnsureInitialized();

        if (_synchronizerCache.TryGetValue(accountId, out var synchronizer))
        {
            return synchronizer.State == AccountSynchronizerState.Synchronizing ||
                   synchronizer.State == AccountSynchronizerState.ExecutingRequests;
        }

        return false;
    }

    /// <summary>
    /// Queues a mail action request to the corresponding account's synchronizer.
    /// </summary>
    /// <param name="request">Request to queue</param>
    /// <param name="accountId">Account ID to queue the request for</param>
    public async Task QueueRequestAsync(IRequestBase request, Guid accountId)
    {
        await QueueRequestAsync(request, accountId, triggerSynchronization: true);
    }

    /// <summary>
    /// Queues a mail action request to the corresponding account's synchronizer with optional synchronization triggering.
    /// </summary>
    /// <param name="request">Request to queue</param>
    /// <param name="accountId">Account ID to queue the request for</param>
    /// <param name="triggerSynchronization">Whether to automatically trigger synchronization after queuing the request</param>
    public async Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization)
    {
        EnsureInitialized();

        var synchronizer = await GetOrCreateSynchronizerAsync(accountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId} to queue request", accountId);
            return;
        }

        _logger.Debug("Queuing request {RequestType} for account {AccountId}",
                     request.GetType().Name, accountId);

        synchronizer.QueueRequest(request);

        if (triggerSynchronization)
        {
            // Trigger synchronization to execute the queued request
            _logger.Debug("Triggering synchronization to execute queued request for account {AccountId}", accountId);

            var synchronizationOptions = new MailSynchronizationOptions()
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            };

            // Trigger synchronization asynchronously without waiting for completion
            // This matches the pattern used in WinoRequestDelegator
            _ = Task.Run(async () =>
            {
                try
                {
                    await SynchronizeMailAsync(synchronizationOptions);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to execute synchronization after queuing request for account {AccountId}", accountId);
                }
            });
        }
    }

    /// <summary>
    /// Handles folder synchronization for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to synchronize folders for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<MailSynchronizationResult> SynchronizeFoldersAsync(Guid accountId,
                                                                         CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var options = new MailSynchronizationOptions
        {
            AccountId = accountId,
            Type = MailSynchronizationType.FoldersOnly
        };

        return await SynchronizeMailAsync(options, cancellationToken);
    }

    /// <summary>
    /// Handles alias synchronization for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to synchronize aliases for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<MailSynchronizationResult> SynchronizeAliasesAsync(Guid accountId,
                                                                         CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var options = new MailSynchronizationOptions
        {
            AccountId = accountId,
            Type = MailSynchronizationType.Alias
        };

        return await SynchronizeMailAsync(options, cancellationToken);
    }

    /// <summary>
    /// Handles profile synchronization for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to synchronize profile for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<MailSynchronizationResult> SynchronizeProfileAsync(Guid accountId,
                                                                         CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var options = new MailSynchronizationOptions
        {
            AccountId = accountId,
            Type = MailSynchronizationType.UpdateProfile
        };

        return await SynchronizeMailAsync(options, cancellationToken);
    }

    /// <summary>
    /// Downloads a MIME message for the given mail item.
    /// </summary>
    /// <param name="mailItem">Mail item to download</param>
    /// <param name="accountId">Account ID that owns the mail item</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Downloaded MIME content path</returns>
    public async Task<string> DownloadMimeMessageAsync(MailCopy mailItem, Guid accountId,
                                                       CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var synchronizer = await GetOrCreateSynchronizerAsync(accountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId} to download MIME", accountId);
            return null;
        }

        _logger.Debug("Downloading MIME message for mail item {MailItemId}", mailItem.Id);

        try
        {
            await synchronizer.DownloadMissingMimeMessageAsync(mailItem, null, cancellationToken);
            return mailItem.Id.ToString(); // Return some identifier, actual implementation might be different
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download MIME message for mail item {MailItemId}", mailItem.Id);
            return null;
        }
    }

    /// <summary>
    /// Creates a new synchronizer for a newly added account.
    /// </summary>
    /// <param name="account">Account to create synchronizer for</param>
    /// <returns>Created synchronizer</returns>
    public Task<IWinoSynchronizerBase> CreateSynchronizerForAccountAsync(MailAccount account)
    {
        EnsureInitialized();

        try
        {
            var synchronizer = _concreteSynchronizerFactory.CreateNewSynchronizer(account);
            _synchronizerCache.TryAdd(account.Id, synchronizer);

            _logger.Information("Created new synchronizer for account {AccountName} ({AccountId})",
                              account.Name, account.Id);

            return Task.FromResult(synchronizer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create synchronizer for account {AccountName} ({AccountId})",
                        account.Name, account.Id);
            return Task.FromResult<IWinoSynchronizerBase>(null);
        }
    }

    /// <summary>
    /// Destroys the synchronizer for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to destroy synchronizer for</param>
    public async Task DestroySynchronizerAsync(Guid accountId)
    {
        EnsureInitialized();

        if (_synchronizerCache.TryRemove(accountId, out var synchronizer))
        {
            try
            {
                await synchronizer.KillSynchronizerAsync();
                _logger.Information("Destroyed synchronizer for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to destroy synchronizer for account {AccountId}", accountId);
            }
        }
    }

    /// <summary>
    /// Gets all cached synchronizers.
    /// </summary>
    /// <returns>Collection of all cached synchronizers</returns>
    public IEnumerable<IWinoSynchronizerBase> GetAllSynchronizers()
    {
        EnsureInitialized();
        return _synchronizerCache.Values.ToList();
    }

    /// <summary>
    /// Gets a synchronizer for the given account ID.
    /// </summary>
    /// <param name="accountId">Account ID</param>
    /// <returns>Synchronizer if found, null otherwise</returns>
    public async Task<IWinoSynchronizerBase> GetSynchronizerAsync(Guid accountId)
    {
        EnsureInitialized();
        return await GetOrCreateSynchronizerAsync(accountId);
    }

    private async Task<IWinoSynchronizerBase> GetOrCreateSynchronizerAsync(Guid accountId)
    {
        if (_synchronizerCache.TryGetValue(accountId, out var existingSynchronizer))
        {
            return existingSynchronizer;
        }

        // Try to create a new synchronizer if not found
        var account = await _accountService.GetAccountAsync(accountId);
        if (account != null)
        {
            return await CreateSynchronizerForAccountAsync(account);
        }

        return null;
    }

    /// <summary>
    /// Handles OAuth authentication for the specified provider.
    /// </summary>
    /// <param name="providerType">The mail provider type to authenticate</param>
    /// <param name="account">Optional account to authenticate (null for initial authentication)</param>
    /// <param name="proposeCopyAuthorizationURL">Whether to propose copying auth URL for Gmail</param>
    /// <returns>Token information containing access token and username</returns>
    public async Task<TokenInformationEx> HandleAuthorizationAsync(MailProviderType providerType,
                                                                  MailAccount account = null,
                                                                  bool proposeCopyAuthorizationURL = false)
    {
        EnsureInitialized();

        try
        {
            var authenticator = _authenticationProvider.GetAuthenticator(providerType);

            // Some users are having issues with Gmail authentication.
            // Their browsers may never launch to complete authentication.
            // Offer to copy auth url for them to complete it manually.
            // Redirection will occur to the app and the token will be saved.
            if (proposeCopyAuthorizationURL && authenticator is IGmailAuthenticator gmailAuthenticator)
            {
                gmailAuthenticator.ProposeCopyAuthURL = true;
            }

            TokenInformationEx tokenInfo;

            if (account != null)
            {
                // Get token for existing account (may trigger interactive auth if token is expired)
                tokenInfo = await authenticator.GetTokenInformationAsync(account);
                _logger.Information("Retrieved token for existing account {AccountAddress}", account.Address);
            }
            else
            {
                // Initial authentication request - there is no account to get token for
                // This will always trigger interactive authentication
                tokenInfo = await authenticator.GenerateTokenInformationAsync(null);
                _logger.Information("Generated new token for {ProviderType} authentication", providerType);
            }

            return tokenInfo;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to handle authorization for {ProviderType}", providerType);
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("SynchronizationManager must be initialized before use. Call InitializeAsync first.");
        }
    }
}
