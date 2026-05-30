using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Telemetry;
using Wino.Messaging.UI;

namespace Wino.Core.Services;

/// <summary>
/// Singleton manager that handles synchronizer instances and operations for all accounts.
/// Replaces the old WinoServerConnectionManager functionality.
/// </summary>
public class SynchronizationManager : ISynchronizationManager, IRecipient<AccountSynchronizerStateChanged>
{
    private static readonly Lazy<SynchronizationManager> _instance = new(() => new SynchronizationManager());
    public static SynchronizationManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<Guid, IWinoSynchronizerBase> _synchronizerCache = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _accountSynchronizationCancellationSources = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _calendarSynchronizationLocks = new();
    private readonly ConcurrentDictionary<Guid, AccountSynchronizationProgress> _mailSynchronizationProgress = new();
    private readonly ConcurrentDictionary<Guid, AccountSynchronizationProgress> _calendarSynchronizationProgress = new();
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<SynchronizationManager>();

    private SynchronizerFactory _concreteSynchronizerFactory;
    private IImapTestService _imapTestService;
    private IAccountService _accountService;
    private IAuthenticationProvider _authenticationProvider;
    private INotificationBuilder _notificationBuilder;
    private IWinoTelemetryService _telemetryService;

    private bool _isInitialized = false;
    private bool _isRegisteredForProgressMessages;

    private SynchronizationManager() { }

    /// <summary>
    /// Initializes the SynchronizationManager with required dependencies.
    /// This must be called before using any other methods.
    /// Note: Synchronizers are created lazily to avoid requiring window handles during app initialization.
    /// </summary>
    /// <param name="synchronizerFactory">Factory for creating synchronizers</param>
    /// <param name="imapTestService">Service for testing IMAP connectivity</param>
    /// <param name="accountService">Service for account operations</param>
    /// <param name="authenticationProvider">Provider for OAuth authentication</param>
    public async Task InitializeAsync(ISynchronizerFactory synchronizerFactory,
                                     IImapTestService imapTestService,
                                     IAccountService accountService,
                                     INotificationBuilder notificationBuilder,
                                     IAuthenticationProvider authenticationProvider,
                                     IWinoTelemetryService telemetryService)
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
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            // DO NOT create synchronizers here to avoid requiring window handles during initialization.
            // Synchronizers will be created lazily when first accessed via GetOrCreateSynchronizerAsync.
            if (!_isRegisteredForProgressMessages)
            {
                WeakReferenceMessenger.Default.Register<AccountSynchronizerStateChanged>(this);
                _isRegisteredForProgressMessages = true;
            }

            _isInitialized = true;
            _logger.Information("SynchronizationManager dependencies initialized. Synchronizers will be created lazily.");
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
            return CreateCertificateUIRequiredResult(sslTestException);
        }
        catch (ImapClientPoolException clientPoolException)
        {
            if (TryGetSslCertificateException(clientPoolException, out var sslTestException))
            {
                _logger.Warning("IMAP connectivity test requires SSL certificate confirmation");
                return CreateCertificateUIRequiredResult(sslTestException);
            }

            _logger.Error(clientPoolException, "IMAP connectivity test failed");
            return ImapConnectivityTestResults.Failure(clientPoolException.InnerException ?? clientPoolException);
        }
        catch (Exception exception)
        {
            if (TryGetSslCertificateException(exception, out var sslTestException))
            {
                _logger.Warning("IMAP connectivity test requires SSL certificate confirmation");
                return CreateCertificateUIRequiredResult(sslTestException);
            }

            _logger.Error(exception, "IMAP connectivity test failed");
            return ImapConnectivityTestResults.Failure(exception);
        }
    }

    internal static bool TryGetSslCertificateException(Exception exception, out ImapTestSSLCertificateException sslException)
    {
        sslException = exception?
            .GetInnerExceptions()
            .OfType<ImapTestSSLCertificateException>()
            .FirstOrDefault();

        return sslException != null;
    }

    private static ImapConnectivityTestResults CreateCertificateUIRequiredResult(ImapTestSSLCertificateException sslTestException)
        => ImapConnectivityTestResults.CertificateUIRequired(
            sslTestException.Issuer,
            sslTestException.ExpirationDateString,
            sslTestException.ValidFromDateString);

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
        var stopwatch = Stopwatch.StartNew();

        if (await IsSynchronizationBlockedByAttentionAsync(options.AccountId).ConfigureAwait(false))
        {
            _logger.Information("Skipping mail synchronization for account {AccountId} because it requires credential attention.", options.AccountId);
            var result = MailSynchronizationResult.Canceled;
            TrackMailSynchronizationSummary(options, null, result, stopwatch.Elapsed);
            return result;
        }

        var synchronizer = await GetOrCreateSynchronizerAsync(options.AccountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId}", options.AccountId);

            var exception = new InvalidOperationException("Can't create/get synchronizer.");
            var result = MailSynchronizationResult
                .Failed(exception)
                .MergeIssues([SynchronizationIssue.FromException(exception, "MailSync")]);
            TrackMailSynchronizationSummary(options, null, result, stopwatch.Elapsed);
            return result;
        }

        _logger.Information("Starting mail synchronization for account {AccountId} with type {SyncType}",
                          options.AccountId, options.Type);

        var accountCancellationSource = _accountSynchronizationCancellationSources.GetOrAdd(options.AccountId, _ => new CancellationTokenSource());
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            accountCancellationSource.Token);

        try
        {
            var result = await synchronizer.SynchronizeMailsAsync(options, linkedCancellationTokenSource.Token);

            _logger.Information("Mail synchronization completed for account {AccountId} with state {State}",
                              options.AccountId, result.CompletedState);

            // Create notifications.
            if (result.DownloadedMessages?.Any() ?? false)
                await _notificationBuilder.CreateNotificationsAsync(result.DownloadedMessages);

            await _notificationBuilder.UpdateTaskbarIconBadgeAsync();

            TrackMailSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Mail synchronization canceled for account {AccountId}", options.AccountId);
            var result = MailSynchronizationResult.Canceled;
            TrackMailSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (AuthenticationAttentionException authEx)
        {
            _logger.Warning("Account {AccountId} requires attention due to authentication issues", options.AccountId);
            await SetInvalidCredentialAttentionAsync(authEx.Account).ConfigureAwait(false);

            // Create app notification for authentication attention
            _notificationBuilder.CreateAttentionRequiredNotification(authEx.Account);

            var result = MailSynchronizationResult
                .Failed(authEx)
                .MergeIssues([SynchronizationIssue.FromException(authEx, "MailSync", SynchronizerErrorSeverity.AuthRequired)]);
            TrackMailSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Mail synchronization failed for account {AccountId}", options.AccountId);
            var result = MailSynchronizationResult
                .Failed(ex)
                .MergeIssues([SynchronizationIssue.FromException(ex, "MailSync")]);
            TrackMailSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        finally
        {
            if (synchronizer.State == AccountSynchronizerState.Idle)
            {
                PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(
                    options.AccountId,
                    SynchronizationProgressCategory.Mail));
            }
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

    public AccountSynchronizationProgress GetSynchronizationProgress(Guid accountId, SynchronizationProgressCategory category)
    {
        EnsureInitialized();

        return category switch
        {
            SynchronizationProgressCategory.Calendar => _calendarSynchronizationProgress.TryGetValue(accountId, out var calendarProgress)
                ? calendarProgress
                : AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Calendar),
            _ => _mailSynchronizationProgress.TryGetValue(accountId, out var mailProgress)
                ? mailProgress
                : AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Mail)
        };
    }

    /// <summary>
    /// Queues a request to the corresponding account's synchronizer with optional synchronization triggering.
    /// Automatically determines whether to trigger mail or calendar synchronization based on the request type.
    /// </summary>
    /// <param name="request">Request to queue</param>
    /// <param name="accountId">Account ID to queue the request for</param>
    /// <param name="triggerSynchronization">Whether to automatically trigger synchronization after queuing the request</param>
    public async Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization)
        => await QueueRequestsAsync([request], accountId, triggerSynchronization).ConfigureAwait(false);

    public async Task QueueRequestsAsync(IEnumerable<IRequestBase> requests, Guid accountId, bool triggerSynchronization)
    {
        EnsureInitialized();

        var requestList = requests?.Where(request => request != null).ToList() ?? [];
        if (requestList.Count == 0)
            return;

        var synchronizer = await GetOrCreateSynchronizerAsync(accountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId} to queue {RequestCount} request(s)", accountId, requestList.Count);
            return;
        }

        if (requestList.Count == 1)
        {
            _logger.Debug("Queuing request {RequestType} for account {AccountId}",
                         requestList[0].GetType().Name, accountId);
        }
        else
        {
            var requestSummary = string.Join(", ", requestList
                .GroupBy(request => request.GetType().Name)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key} x{group.Count()}"));

            _logger.Debug("Queuing {RequestCount} requests for account {AccountId}: {RequestSummary}",
                         requestList.Count, accountId, requestSummary);
        }

        foreach (var request in requestList)
        {
            synchronizer.QueueRequest(request);
        }

        if (triggerSynchronization)
        {
            // Determine if this is a calendar or mail operation
            bool isCalendarOperation = requestList.All(request => request is ICalendarActionRequest);

            if (isCalendarOperation)
            {
                // Trigger calendar synchronization
                _logger.Debug("Triggering calendar synchronization to execute queued request for account {AccountId}", accountId);

                var calendarSyncOptions = new CalendarSynchronizationOptions()
                {
                    AccountId = accountId
                };

                // Trigger synchronization asynchronously without waiting for completion
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SynchronizeCalendarAsync(calendarSyncOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to execute calendar synchronization after queuing request for account {AccountId}", accountId);
                    }
                });
            }
            else
            {
                // Trigger mail synchronization (includes mail and folder operations)
                _logger.Debug("Triggering mail synchronization to execute queued request for account {AccountId}", accountId);

                var mailSyncOptions = new MailSynchronizationOptions()
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
                        await SynchronizeMailAsync(mailSyncOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to execute mail synchronization after queuing request for account {AccountId}", accountId);
                    }
                });
            }
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
    /// Handles category synchronization for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to synchronize categories for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<MailSynchronizationResult> SynchronizeCategoriesAsync(Guid accountId,
                                                                            CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var options = new MailSynchronizationOptions
        {
            AccountId = accountId,
            Type = MailSynchronizationType.Categories
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
    /// Handles calendar synchronization for the given account.
    /// </summary>
    /// <param name="options">Calendar synchronization options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synchronization result</returns>
    public async Task<CalendarSynchronizationResult> SynchronizeCalendarAsync(CalendarSynchronizationOptions options,
                                                                               CancellationToken cancellationToken = default)
        => options.Type == CalendarSynchronizationType.Strict
            ? await SynchronizeCalendarStrictAsync(options, cancellationToken).ConfigureAwait(false)
            : await RunCalendarSynchronizationWithLockAsync(
                options.AccountId,
                cancellationToken,
                () => SynchronizeCalendarCoreAsync(options, cancellationToken, reportState: true)).ConfigureAwait(false);

    private async Task<CalendarSynchronizationResult> SynchronizeCalendarStrictAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken)
    {
        var metadataOptions = new CalendarSynchronizationOptions
        {
            AccountId = options.AccountId,
            Type = CalendarSynchronizationType.CalendarMetadata,
            SynchronizationCalendarIds = options.SynchronizationCalendarIds
        };

        var eventOptions = new CalendarSynchronizationOptions
        {
            AccountId = options.AccountId,
            Type = CalendarSynchronizationType.CalendarEvents,
            SynchronizationCalendarIds = options.SynchronizationCalendarIds
        };

        return await RunCalendarSynchronizationWithLockAsync(options.AccountId, cancellationToken, async () =>
        {
            try
            {
                PublishCalendarSynchronizationState(
                    options.AccountId,
                    CalendarSynchronizationType.Strict,
                    isSynchronizationInProgress: true,
                    Translator.SyncAction_SynchronizingCalendarMetadata);

                var metadataResult = await SynchronizeCalendarCoreAsync(metadataOptions, cancellationToken, reportState: false).ConfigureAwait(false);
                if (metadataResult.CompletedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.Canceled)
                {
                    return metadataResult;
                }

                PublishCalendarSynchronizationState(
                    options.AccountId,
                    CalendarSynchronizationType.Strict,
                    isSynchronizationInProgress: true,
                    Translator.SyncAction_SynchronizingCalendarEvents);

                return await SynchronizeCalendarCoreAsync(eventOptions, cancellationToken, reportState: false).ConfigureAwait(false);
            }
            finally
            {
                PublishCalendarSynchronizationState(options.AccountId, CalendarSynchronizationType.Strict, isSynchronizationInProgress: false);
            }
        }).ConfigureAwait(false);
    }

    private async Task<CalendarSynchronizationResult> SynchronizeCalendarCoreAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken,
        bool reportState)
    {
        EnsureInitialized();
        var stopwatch = Stopwatch.StartNew();

        if (await IsSynchronizationBlockedByAttentionAsync(options.AccountId).ConfigureAwait(false))
        {
            _logger.Information("Skipping calendar synchronization for account {AccountId} because it requires credential attention.", options.AccountId);
            var result = CalendarSynchronizationResult.Canceled;
            TrackCalendarSynchronizationSummary(options, null, result, stopwatch.Elapsed);
            return result;
        }

        var synchronizer = await GetOrCreateSynchronizerAsync(options.AccountId);
        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId}", options.AccountId);
            var exception = new InvalidOperationException("Can't create/get synchronizer.");
            var result = CalendarSynchronizationResult
                .Failed(exception)
                .MergeIssues([SynchronizationIssue.FromException(exception, "CalendarSync")]);
            TrackCalendarSynchronizationSummary(options, null, result, stopwatch.Elapsed);
            return result;
        }

        _logger.Information("Starting calendar synchronization for account {AccountId} with type {SyncType}",
                          options.AccountId, options.Type);

        if (reportState)
        {
            PublishCalendarSynchronizationState(
                options.AccountId,
                options.Type,
                isSynchronizationInProgress: true,
                GetCalendarSynchronizationStatus(options.Type));
        }

        var accountCancellationSource = _accountSynchronizationCancellationSources.GetOrAdd(options.AccountId, _ => new CancellationTokenSource());
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            accountCancellationSource.Token);

        try
        {
            var result = await synchronizer.SynchronizeCalendarEventsAsync(options, linkedCancellationTokenSource.Token);
            var downloadedEventCount = result.DownloadedEvents?.Count() ?? 0;

            _logger.Information("Calendar synchronization completed for account {AccountId} with state {State}",
                              options.AccountId, result.CompletedState);

            if (downloadedEventCount > 0)
            {
                await _notificationBuilder.AddCalendarTaskbarBadgeCountAsync(downloadedEventCount).ConfigureAwait(false);
            }

            TrackCalendarSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Calendar synchronization canceled for account {AccountId}", options.AccountId);
            var result = CalendarSynchronizationResult.Canceled;
            TrackCalendarSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (AuthenticationAttentionException authEx)
        {
            _logger.Warning("Account {AccountId} requires attention due to authentication issues", options.AccountId);
            await SetInvalidCredentialAttentionAsync(authEx.Account).ConfigureAwait(false);

            // Create app notification for authentication attention
            _notificationBuilder.CreateAttentionRequiredNotification(authEx.Account);

            var result = CalendarSynchronizationResult
                .Failed(authEx)
                .MergeIssues([SynchronizationIssue.FromException(authEx, "CalendarSync", SynchronizerErrorSeverity.AuthRequired)]);
            TrackCalendarSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar synchronization failed for account {AccountId}", options.AccountId);
            var result = CalendarSynchronizationResult
                .Failed(ex)
                .MergeIssues([SynchronizationIssue.FromException(ex, "CalendarSync")]);
            TrackCalendarSynchronizationSummary(options, synchronizer, result, stopwatch.Elapsed);
            return result;
        }
        finally
        {
            if (reportState)
            {
                PublishCalendarSynchronizationState(options.AccountId, options.Type, isSynchronizationInProgress: false);
            }
        }
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
        catch (SynchronizerEntityNotFoundException)
        {
            _logger.Warning("MIME message for mail item {MailItemId} no longer exists on server. Removed locally.", mailItem.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download MIME message for mail item {MailItemId}", mailItem.Id);
            return null;
        }
    }

    /// <summary>
    /// Downloads a calendar attachment using the appropriate synchronizer.
    /// </summary>
    public async Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (calendarItem == null)
            throw new ArgumentNullException(nameof(calendarItem));

        if (attachment == null)
            throw new ArgumentNullException(nameof(attachment));

        var accountId = calendarItem.AssignedCalendar?.AccountId ?? Guid.Empty;
        if (accountId == Guid.Empty)
            throw new InvalidOperationException("Calendar item does not have an assigned account.");

        var synchronizer = await GetOrCreateSynchronizerAsync(accountId);

        if (synchronizer == null)
        {
            _logger.Error("Could not find or create synchronizer for account {AccountId} to download calendar attachment", accountId);
            throw new InvalidOperationException("No synchronizer available for downloading calendar attachment.");
        }

        _logger.Debug("Downloading calendar attachment {AttachmentId} for calendar item {CalendarItemId}",
                     attachment.Id, calendarItem.Id);

        try
        {
            await synchronizer.DownloadCalendarAttachmentAsync(
                calendarItem,
                attachment,
                localFilePath,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to download calendar attachment {AttachmentId}", attachment.Id);
            throw;
        }
    }

    /// <summary>
    /// Creates a new synchronizer for a newly added account.
    /// </summary>
    /// <param name="account">Account to create synchronizer for</param>
    /// <returns>Created synchronizer</returns>
    public IWinoSynchronizerBase CreateSynchronizerForAccount(MailAccount account)
    {
        EnsureInitialized();

        try
        {
            var synchronizer = _concreteSynchronizerFactory.CreateNewSynchronizer(account);
            _synchronizerCache.TryAdd(account.Id, synchronizer);

            _logger.Information("Created new synchronizer for account {AccountName} ({AccountId})",
                              account.Name, account.Id);

            return synchronizer;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create synchronizer for account {AccountName} ({AccountId})",
                        account.Name, account.Id);
            return null;
        }
    }

    /// <summary>
    /// Cancels all in-flight synchronizations for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to cancel synchronizations for</param>
    public Task CancelSynchronizationsAsync(Guid accountId)
    {
        EnsureInitialized();

        if (_accountSynchronizationCancellationSources.TryRemove(accountId, out var cancellationSource))
        {
            try
            {
                if (!cancellationSource.IsCancellationRequested)
                {
                    cancellationSource.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // no-op
            }
            finally
            {
                cancellationSource.Dispose();
            }

            _logger.Information("Canceled ongoing synchronizations for account {AccountId}", accountId);
        }

        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Mail));
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Calendar));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Destroys the synchronizer for the given account.
    /// </summary>
    /// <param name="accountId">Account ID to destroy synchronizer for</param>
    public async Task DestroySynchronizerAsync(Guid accountId)
    {
        EnsureInitialized();
        await CancelSynchronizationsAsync(accountId);

        if (_synchronizerCache.TryRemove(accountId, out var synchronizer))
        {
            try
            {
                await synchronizer.KillSynchronizerAsync();
                _logger.Information("Destroyed synchronizer for account {AccountId}", accountId);
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Synchronizer destruction canceled for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to destroy synchronizer for account {AccountId}", accountId);
            }
        }

        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Mail));
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Calendar));
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

    private void TrackMailSynchronizationSummary(
        MailSynchronizationOptions options,
        IWinoSynchronizerBase synchronizer,
        MailSynchronizationResult result,
        TimeSpan duration)
    {
        if (_telemetryService == null || options == null || result == null)
            return;

        var properties = CreateSynchronizationTelemetryProperties(
            synchronizer?.Account,
            "mail",
            options.Type.ToString(),
            result.CompletedState,
            duration);

        properties["successful_folder_count"] = result.SuccessfulFolderCount.ToString();
        properties["failed_folder_count"] = result.FailedFolderCount.ToString();
        properties["total_folder_count"] = result.FolderResults.Count.ToString();
        properties["downloaded_count_bucket"] = GetCountBucket(result.TotalDownloadedCount);
        properties["updated_count_bucket"] = GetCountBucket(result.TotalUpdatedCount);
        properties["deleted_count_bucket"] = GetCountBucket(result.TotalDeletedCount);

        AddIssueTelemetry(properties, result.AllIssues?.ToList(), result.Exception);

        _telemetryService.TrackEvent(
            "sync_summary",
            properties,
            GetTelemetryLevel(result.CompletedState));
    }

    private void TrackCalendarSynchronizationSummary(
        CalendarSynchronizationOptions options,
        IWinoSynchronizerBase synchronizer,
        CalendarSynchronizationResult result,
        TimeSpan duration)
    {
        if (_telemetryService == null || options == null || result == null)
            return;

        var properties = CreateSynchronizationTelemetryProperties(
            synchronizer?.Account,
            "calendar",
            options.Type.ToString(),
            result.CompletedState,
            duration);

        properties["downloaded_count_bucket"] = GetCountBucket(result.DownloadedEvents?.Count() ?? 0);
        AddIssueTelemetry(properties, result.AllIssues?.ToList(), result.Exception);

        _telemetryService.TrackEvent(
            "sync_summary",
            properties,
            GetTelemetryLevel(result.CompletedState));
    }

    private static Dictionary<string, string> CreateSynchronizationTelemetryProperties(
        MailAccount account,
        string syncArea,
        string syncType,
        SynchronizationCompletedState completedState,
        TimeSpan duration)
    {
        var properties = new Dictionary<string, string>
        {
            ["feature"] = "sync",
            ["sync_area"] = syncArea,
            ["sync_type"] = syncType,
            ["state"] = completedState.ToString(),
            ["duration_bucket"] = GetDurationBucket(duration),
            ["provider"] = account?.ProviderType.ToString() ?? "unknown",
            ["special_provider"] = account?.SpecialImapProvider.ToString() ?? "unknown",
            ["mail_enabled"] = (account?.IsMailAccessGranted == true).ToString(),
            ["calendar_enabled"] = (account?.IsCalendarAccessGranted == true).ToString()
        };

        if (account?.ProviderType == MailProviderType.IMAP4 && account.ServerInformation != null)
        {
            foreach (var property in ImapSetupTelemetrySanitizer.CreateServerProperties(account.ServerInformation))
            {
                properties[property.Key] = property.Value;
            }

            properties["feature"] = "sync";
        }

        return properties;
    }

    private static void AddIssueTelemetry(
        IDictionary<string, string> properties,
        IReadOnlyCollection<SynchronizationIssue> issues,
        Exception exception)
    {
        var firstIssue = issues?.FirstOrDefault();

        properties["issue_count"] = (issues?.Count ?? 0).ToString();

        if (firstIssue != null)
        {
            properties["issue_category"] = firstIssue.Category.ToString();
            properties["issue_severity"] = firstIssue.Severity.ToString();

            if (!string.IsNullOrWhiteSpace(firstIssue.ExceptionType))
                properties["exception_type"] = firstIssue.ExceptionType;
        }

        if (exception != null && !properties.ContainsKey("exception_type"))
            properties["exception_type"] = exception.GetType().Name;
    }

    private static WinoTelemetryLevel GetTelemetryLevel(SynchronizationCompletedState completedState)
        => completedState switch
        {
            SynchronizationCompletedState.Failed => WinoTelemetryLevel.Warning,
            SynchronizationCompletedState.PartiallyCompleted => WinoTelemetryLevel.Warning,
            _ => WinoTelemetryLevel.Info
        };

    private static string GetDurationBucket(TimeSpan duration)
        => duration.TotalSeconds switch
        {
            < 1 => "<1s",
            < 5 => "1-5s",
            < 30 => "5-30s",
            < 120 => "30s-2m",
            < 600 => "2-10m",
            < 1800 => "10-30m",
            _ => "30m+"
        };

    private static string GetCountBucket(int count)
        => count switch
        {
            <= 0 => "0",
            1 => "1",
            <= 10 => "2-10",
            <= 100 => "11-100",
            <= 1000 => "101-1000",
            _ => "1000+"
        };

    private async Task<IWinoSynchronizerBase> GetOrCreateSynchronizerAsync(Guid accountId)
    {
        if (_synchronizerCache.TryGetValue(accountId, out var existingSynchronizer))
        {
            var currentAccount = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            if (currentAccount != null && RequiresSynchronizerRefresh(existingSynchronizer.Account, currentAccount))
            {
                await DestroySynchronizerAsync(accountId).ConfigureAwait(false);
                return CreateSynchronizerForAccount(currentAccount);
            }

            return existingSynchronizer;
        }

        // Try to create a new synchronizer if not found
        var account = await _accountService.GetAccountAsync(accountId);
        if (account != null)
        {
            return CreateSynchronizerForAccount(account);
        }

        return null;
    }

    public static bool CanSynchronizeCalendar(MailAccount account)
        => account?.IsCalendarAccessGranted == true;

    public static bool RequiresSynchronizerRefresh(MailAccount cachedAccount, MailAccount currentAccount)
        => cachedAccount == null ||
           currentAccount == null ||
           cachedAccount.IsMailAccessGranted != currentAccount.IsMailAccessGranted ||
           cachedAccount.IsCalendarAccessGranted != currentAccount.IsCalendarAccessGranted;

    /// <summary>
    /// Handles OAuth authentication for the specified provider.
    /// </summary>
    /// <param name="providerType">The mail provider type to authenticate</param>
    /// <param name="account">Optional account to authenticate (null for initial authentication)</param>
    /// <param name="proposeCopyAuthorizationURL">Whether to propose copying auth URL for Gmail</param>
    /// <returns>Token information containing access token and username</returns>
    public async Task<TokenInformationEx> HandleAuthorizationAsync(MailProviderType providerType,
                                                                  MailAccount account = null,
                                                                  bool proposeCopyAuthorizationURL = false,
                                                                  bool forceInteractive = false)
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
                // Get token for existing account. Capability upgrades must force a fresh
                // consent prompt so the locally cached Google token cannot keep old scopes.
                if (forceInteractive)
                {
                    await authenticator.DeleteTokenInformationAsync(account).ConfigureAwait(false);
                    tokenInfo = await authenticator.GenerateTokenInformationAsync(account).ConfigureAwait(false);
                }
                else
                {
                    tokenInfo = await authenticator.GetTokenInformationAsync(account).ConfigureAwait(false);
                }
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

    public void Receive(AccountSynchronizerStateChanged message)
    {
        var totalUnits = Math.Max(0, message.TotalItemsToSync);
        var remainingUnits = totalUnits > 0
            ? Math.Clamp(message.RemainingItemsToSync, 0, totalUnits)
            : 0;

        var isInProgress = message.NewState != AccountSynchronizerState.Idle;
        var isIndeterminate = isInProgress && totalUnits <= 0;
        var progressPercentage = totalUnits > 0
            ? ((double)(totalUnits - remainingUnits) / totalUnits) * 100
            : 0;

        var progress = new AccountSynchronizationProgress(
            message.AccountId,
            message.ProgressCategory,
            isInProgress,
            isIndeterminate,
            progressPercentage,
            totalUnits,
            remainingUnits,
            BuildSynchronizationStatus(message.ProgressCategory, message.NewState, totalUnits, progressPercentage, message.SynchronizationStatus),
            message.NewState);

        PublishSynchronizationProgress(progress);
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("SynchronizationManager must be initialized before use. Call InitializeAsync first.");
        }
    }

    private async Task SetInvalidCredentialAttentionAsync(MailAccount account)
    {
        if (account == null || _accountService == null)
            return;

        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);

        if (persistedAccount == null)
            return;

        if (persistedAccount.AttentionReason == AccountAttentionReason.InvalidCredentials)
            return;

        persistedAccount.AttentionReason = AccountAttentionReason.InvalidCredentials;
        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
    }

    private async Task<bool> IsSynchronizationBlockedByAttentionAsync(Guid accountId)
    {
        if (_accountService == null)
            return false;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        return account?.AttentionReason == AccountAttentionReason.InvalidCredentials;
    }

    private void PublishSynchronizationProgress(AccountSynchronizationProgress progress)
    {
        var normalized = progress.IsInProgress
            ? progress
            : AccountSynchronizationProgress.Idle(progress.AccountId, progress.Category);

        var cache = normalized.Category == SynchronizationProgressCategory.Calendar
            ? _calendarSynchronizationProgress
            : _mailSynchronizationProgress;

        cache.AddOrUpdate(normalized.AccountId, normalized, (_, _) => normalized);

        WeakReferenceMessenger.Default.Send(new AccountSynchronizationProgressUpdatedMessage(normalized));
    }

    private static string BuildSynchronizationStatus(
        SynchronizationProgressCategory category,
        AccountSynchronizerState state,
        int totalUnits,
        double progressPercentage,
        string rawStatus)
    {
        if (state == AccountSynchronizerState.Idle)
            return string.Empty;

        if (state == AccountSynchronizerState.ExecutingRequests)
            return Translator.SynchronizationProgress_ApplyingChanges;

        if (totalUnits > 0)
        {
            var roundedProgress = (int)Math.Round(progressPercentage, MidpointRounding.AwayFromZero);

            return category == SynchronizationProgressCategory.Calendar
                ? string.Format(Translator.SynchronizationProgress_CalendarPercent, roundedProgress)
                : string.Format(Translator.SynchronizationProgress_MailPercent, roundedProgress);
        }

        if (category == SynchronizationProgressCategory.Calendar && !string.IsNullOrWhiteSpace(rawStatus))
            return rawStatus;

        return category == SynchronizationProgressCategory.Calendar
            ? Translator.SynchronizationProgress_CalendarInProgress
            : Translator.SynchronizationProgress_MailInProgress;
    }

    private void PublishCalendarSynchronizationState(
        Guid accountId,
        CalendarSynchronizationType synchronizationType,
        bool isSynchronizationInProgress,
        string synchronizationStatus = "")
    {
        PublishSynchronizationProgress(new AccountSynchronizationProgress(
            accountId,
            SynchronizationProgressCategory.Calendar,
            isSynchronizationInProgress,
            isSynchronizationInProgress,
            0,
            0,
            0,
            synchronizationStatus,
            isSynchronizationInProgress ? AccountSynchronizerState.Synchronizing : AccountSynchronizerState.Idle));
    }

    private static string GetCalendarSynchronizationStatus(CalendarSynchronizationType synchronizationType)
        => synchronizationType switch
        {
            CalendarSynchronizationType.CalendarMetadata => Translator.SyncAction_SynchronizingCalendarMetadata,
            CalendarSynchronizationType.Strict => Translator.SyncAction_SynchronizingCalendarData,
            _ => Translator.SyncAction_SynchronizingCalendarEvents
        };

    private async Task<CalendarSynchronizationResult> RunCalendarSynchronizationWithLockAsync(
        Guid accountId,
        CancellationToken cancellationToken,
        Func<Task<CalendarSynchronizationResult>> synchronizationFactory)
    {
        var calendarSemaphore = _calendarSynchronizationLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await calendarSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await synchronizationFactory().ConfigureAwait(false);
        }
        finally
        {
            calendarSemaphore.Release();
        }
    }
}
