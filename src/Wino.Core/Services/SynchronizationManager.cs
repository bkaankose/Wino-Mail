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
using Wino.Core.Helpers;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;
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
    private readonly ConcurrentDictionary<Guid, AccountSynchronizationProgress> _contactSynchronizationProgress = new();
    private readonly ConcurrentDictionary<Guid, AccountSynchronizationProgress> _taskSynchronizationProgress = new();
    private readonly ConcurrentDictionary<Guid, PendingUndoActionPack> _pendingUndoActionPacks = new();
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private readonly object _undoActionPackLock = new();
    private readonly ILogger _logger = Log.ForContext<SynchronizationManager>();

    private SynchronizerFactory _concreteSynchronizerFactory;
    private IImapTestService _imapTestService;
    private IAccountService _accountService;
    private IAuthenticationProvider _authenticationProvider;
    private INotificationBuilder _notificationBuilder;
    private IWinoTelemetryService _telemetryService;
    private IPreferencesService _preferencesService;
    private IDraftSyncRetryService _draftSyncRetryService;

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
                                     IWinoTelemetryService telemetryService,
                                     IPreferencesService preferencesService,
                                     IDraftSyncRetryService draftSyncRetryService)
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
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _draftSyncRetryService = draftSyncRetryService ?? throw new ArgumentNullException(nameof(draftSyncRetryService));

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
    /// <returns>Test results indicating success or failure with details</returns>
    public async Task<ImapConnectivityTestResults> TestImapConnectivityAsync(CustomServerInformation serverInformation)
    {
        EnsureInitialized();

        try
        {
            _logger.Information("Testing IMAP connectivity for {Server}:{Port}",
                              serverInformation.IncomingServer,
                              serverInformation.IncomingServerPort);

            await _imapTestService.TestImapConnectionAsync(serverInformation);

            _logger.Information("IMAP connectivity test successful");
            return ImapConnectivityTestResults.Success();
        }
        catch (MailServerCertificateException certificateException)
        {
            _logger.Warning("Mail server connectivity test requires certificate confirmation for {Protocol} {Host}:{Port}",
                certificateException.Failure.Protocol,
                certificateException.Failure.Host,
                certificateException.Failure.Port);
            return ImapConnectivityTestResults.CertificateUIRequired(certificateException.Failure);
        }
        catch (ImapTestSSLCertificateException sslTestException)
        {
            _logger.Warning("IMAP connectivity test requires SSL certificate confirmation");
            return CreateCertificateUIRequiredResult(sslTestException);
        }
        catch (ImapClientPoolException clientPoolException)
        {
            if (TryGetCertificateException(clientPoolException, out var certificateException))
                return ImapConnectivityTestResults.CertificateUIRequired(certificateException.Failure);

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
            if (TryGetCertificateException(exception, out var certificateException))
                return ImapConnectivityTestResults.CertificateUIRequired(certificateException.Failure);

            if (TryGetSslCertificateException(exception, out var sslTestException))
            {
                _logger.Warning("IMAP connectivity test requires SSL certificate confirmation");
                return CreateCertificateUIRequiredResult(sslTestException);
            }

            _logger.Error(exception, "IMAP connectivity test failed");
            return ImapConnectivityTestResults.Failure(exception);
        }
    }

    internal static bool TryGetCertificateException(Exception exception, out MailServerCertificateException certificateException)
    {
        certificateException = exception?
            .GetInnerExceptions()
            .OfType<MailServerCertificateException>()
            .FirstOrDefault();
        return certificateException != null;
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

        if (options.Type == MailSynchronizationType.ExecuteRequests && HasPendingUndoAction(options.AccountId))
        {
            var pendingSynchronizer = await GetOrCreateSynchronizerAsync(options.AccountId).ConfigureAwait(false);

            if (pendingSynchronizer?.HasQueuedRequests() != true)
            {
                _logger.Debug("Deferring ExecuteRequests synchronization for account {AccountId} because an undoable action is still in its grace period.", options.AccountId);

                var result = MailSynchronizationResult.Canceled;
                TrackMailSynchronizationSummary(options, pendingSynchronizer, result, stopwatch.Elapsed);
                return result;
            }
        }

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

        if (options.Type is MailSynchronizationType.ExecuteRequests or MailSynchronizationType.FullFolders)
        {
            try
            {
                var queuedRetries = await _draftSyncRetryService
                    .QueueEligibleRetriesAsync(options.AccountId, synchronizer)
                    .ConfigureAwait(false);

                if (queuedRetries && options.Type == MailSynchronizationType.FullFolders)
                {
                    WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                    {
                        AccountId = options.AccountId,
                        Type = MailSynchronizationType.ExecuteRequests
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Draft retry sweep failed for account {AccountId}", options.AccountId);
            }
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
            if (TryGetCertificateException(ex, out _))
            {
                await SetAttentionAsync(synchronizer.Account, AccountAttentionReason.CertificateValidationFailed).ConfigureAwait(false);
                _notificationBuilder.CreateAttentionRequiredNotification(synchronizer.Account);
            }

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
            SynchronizationProgressCategory.Contacts => _contactSynchronizationProgress.TryGetValue(accountId, out var contactProgress)
                ? contactProgress
                : AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Contacts),
            SynchronizationProgressCategory.Tasks => _taskSynchronizationProgress.TryGetValue(accountId, out var taskProgress)
                ? taskProgress
                : AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Tasks),
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
        => await QueueRequestPackAsync(
            new Dictionary<Guid, List<IRequestBase>>
            {
                [accountId] = requests?.Where(request => request != null).ToList() ?? []
            },
            triggerSynchronization).ConfigureAwait(false);

    public async Task QueueRequestPackAsync(IReadOnlyDictionary<Guid, List<IRequestBase>> requestsByAccount, bool triggerSynchronization)
    {
        EnsureInitialized();

        var normalizedRequestsByAccount = requestsByAccount?
            .Where(pair => pair.Value?.Any(request => request != null) == true)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Where(request => request != null).ToList()) ?? [];

        if (normalizedRequestsByAccount.Count == 0)
            return;

        var allRequests = normalizedRequestsByAccount.Values.SelectMany(requests => requests).ToList();
        var undoActionSettings = CreateUndoActionSettings(allRequests);

        if (undoActionSettings != null)
        {
            QueueUndoActionPack(normalizedRequestsByAccount, undoActionSettings);
            return;
        }

        foreach (var pair in normalizedRequestsByAccount)
        {
            await QueueRequestsCoreAsync(pair.Value, pair.Key, triggerSynchronization).ConfigureAwait(false);
        }
    }

    private async Task QueueRequestsCoreAsync(IEnumerable<IRequestBase> requests, Guid accountId, bool triggerSynchronization)
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
            PublishSynchronizationRequests(accountId, requestList);
        }
    }

    public Task UndoLatestQueuedAction(IWinoSynchronizerBase synchronizer)
        => synchronizer == null
            ? Task.CompletedTask
            : UndoLatestQueuedAction(synchronizer.Account.Id);

    public bool IsDeleteRequestQueued(Guid accountId, Guid uniqueMailId)
    {
        lock (_undoActionPackLock)
        {
            return _pendingUndoActionPacks.Values
                .Where(pack => !pack.IsCompleted)
                .SelectMany(pack => pack.RequestsByAccount.TryGetValue(accountId, out var requests)
                    ? requests
                    : [])
                .OfType<DeleteRequest>()
                .Any(request => request.Item?.UniqueId == uniqueMailId);
        }
    }

    public Task UndoLatestQueuedAction(Guid accountId)
    {
        EnsureInitialized();

        PendingUndoActionPack pendingPack = null;

        lock (_undoActionPackLock)
        {
            pendingPack = _pendingUndoActionPacks.Values
                .Where(pack => !pack.IsCompleted && pack.RequestsByAccount.ContainsKey(accountId))
                .OrderByDescending(pack => pack.CreatedAt)
                .FirstOrDefault();

            if (pendingPack == null)
                return Task.CompletedTask;

            pendingPack.IsCompleted = true;
            _pendingUndoActionPacks.TryRemove(pendingPack.Id, out _);
        }

        pendingPack.CancellationTokenSource.Cancel();
        pendingPack.CancellationTokenSource.Dispose();

        RequestUiChangeCoordinator.RevertRequests(pendingPack.RequestsByAccount.Values.SelectMany(requests => requests));
        PublishUndoableMailActionPackChanged(pendingPack, UndoableMailActionPackState.Undone);

        _logger.Information("Undid queued action pack {PackId} for account {AccountId}", pendingPack.Id, accountId);
        return Task.CompletedTask;
    }

    private void QueueUndoActionPack(Dictionary<Guid, List<IRequestBase>> requestsByAccount, UndoActionSettings undoActionSettings)
    {
        var allRequests = requestsByAccount.Values.SelectMany(requests => requests).ToList();
        var pack = new PendingUndoActionPack
        {
            Id = Guid.NewGuid(),
            RequestsByAccount = requestsByAccount,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(undoActionSettings.IntervalInSeconds),
            IntervalInSeconds = undoActionSettings.IntervalInSeconds,
            RequiresFolderSynchronizationByAccount = requestsByAccount
                .Where(pair => pair.Value.Any(RequiresFolderRefreshAfterExecution))
                .Select(pair => pair.Key)
                .ToHashSet()
        };
        pack.VisibleMailActionPack = CreateVisibleMailActionPack(pack, undoActionSettings);

        RequestUiChangeCoordinator.ApplyRequests(allRequests);

        lock (_undoActionPackLock)
        {
            _pendingUndoActionPacks[pack.Id] = pack;
        }

        _ = RunUndoActionPackTimerAsync(pack);
        PublishUndoableMailActionPackChanged(pack, UndoableMailActionPackState.Queued);

        _logger.Debug("Queued undoable action pack {PackId} with {RequestCount} request(s) for {AccountCount} account(s)",
            pack.Id,
            allRequests.Count,
            requestsByAccount.Count);
    }

    private async Task RunUndoActionPackTimerAsync(PendingUndoActionPack pack)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(pack.IntervalInSeconds), pack.CancellationTokenSource.Token).ConfigureAwait(false);
            await PromoteUndoActionPackAsync(pack.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PromoteUndoActionPackAsync(Guid packId)
    {
        PendingUndoActionPack pack = null;

        lock (_undoActionPackLock)
        {
            if (!_pendingUndoActionPacks.TryRemove(packId, out pack) || pack.IsCompleted)
                return;

            pack.IsCompleted = true;
        }

        pack.CancellationTokenSource.Dispose();
        PublishUndoableMailActionPackChanged(pack, UndoableMailActionPackState.Expired);

        foreach (var pair in pack.RequestsByAccount)
        {
            await QueueRequestsCoreAsync(pair.Value, pair.Key, triggerSynchronization: true).ConfigureAwait(false);

            if (pack.RequiresFolderSynchronizationByAccount.Contains(pair.Key))
            {
                WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = pair.Key,
                    Type = MailSynchronizationType.FoldersOnly
                }));
            }
        }

        _logger.Debug("Promoted undoable action pack {PackId} for execution", pack.Id);
    }

    private bool HasPendingUndoAction(Guid accountId)
    {
        lock (_undoActionPackLock)
        {
            return _pendingUndoActionPacks.Values.Any(pack => !pack.IsCompleted && pack.RequestsByAccount.ContainsKey(accountId));
        }
    }

    private static bool RequiresFolderRefreshAfterExecution(IRequestBase request)
        => request is DeleteFolderRequest or CreateSubFolderRequest or CreateRootFolderRequest;

    private void PublishSynchronizationRequests(Guid accountId, IReadOnlyCollection<IRequestBase> requests)
    {
        var hasCalendarRequests = requests.Any(request => request is ICalendarActionRequest);
        var hasContactRequests = requests.Any(request => request is IContactActionRequest);
        var hasTaskRequests = requests.Any(request => request is ITaskActionRequest);
        var hasMailRequests = requests.Any(request => request is IMailActionRequest or IFolderActionRequest or ICategoryActionRequest);

        if (hasCalendarRequests)
        {
            _logger.Debug("Publishing calendar synchronization request for account {AccountId}", accountId);
            WeakReferenceMessenger.Default.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = accountId,
                Type = CalendarSynchronizationType.ExecuteRequests
            }));
        }

        if (hasContactRequests)
        {
            foreach (var addressBookId in requests.OfType<IContactActionRequest>().Select(request => request.AddressBookId).Distinct())
            {
                _logger.Debug("Publishing contact synchronization request for account {AccountId} and address book {AddressBookId}", accountId, addressBookId);
                WeakReferenceMessenger.Default.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
                {
                    AccountId = accountId,
                    AddressBookId = addressBookId,
                    Type = ContactSynchronizationType.ExecuteRequests
                }));
            }
        }

        if (hasTaskRequests)
        {
            _logger.Debug("Publishing task synchronization request for account {AccountId}", accountId);
            WeakReferenceMessenger.Default.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = accountId,
                Type = TaskSynchronizationType.ExecuteRequests
            }));
        }

        if (hasMailRequests || (!hasCalendarRequests && !hasContactRequests && !hasTaskRequests))
        {
            _logger.Debug("Publishing mail synchronization request for account {AccountId}", accountId);
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            }));
        }
    }

    private UndoActionSettings CreateUndoActionSettings(IReadOnlyCollection<IRequestBase> allRequests)
    {
        if (allRequests.Count == 0)
            return null;

        if (allRequests.All(request => request is SendDraftRequest))
        {
            if (_preferencesService?.IsUndoSendingDraftsEnabled != true)
                return null;

            return new UndoActionSettings(
                Translator.UndoActions_SendDraftInfoBarTitle,
                Translator.UndoActions_SendDraftInfoBarMessage,
                InfoBarMessageType.Information,
                Math.Clamp(_preferencesService?.UndoSendingDraftsIntervalInSeconds ?? 5, 1, 10));
        }

        if (allRequests.All(IsDeleteMailRequest))
        {
            if (_preferencesService?.IsUndoDeletingMailsEnabled != true)
                return null;

            return new UndoActionSettings(
                Translator.UndoActions_DeleteMailInfoBarTitle,
                Translator.UndoActions_DeleteMailInfoBarMessage,
                InfoBarMessageType.Error,
                Math.Clamp(_preferencesService?.UndoDeletingMailsIntervalInSeconds ?? 5, 1, 10));
        }

        return null;
    }

    private static UndoableMailActionPack CreateVisibleMailActionPack(PendingUndoActionPack pack, UndoActionSettings undoActionSettings)
    {
        return new UndoableMailActionPack(
            pack.Id,
            pack.RequestsByAccount.Keys.ToList(),
            undoActionSettings.Title,
            undoActionSettings.Severity,
            pack.ExpiresAt,
            pack.IntervalInSeconds);
    }

    private static bool IsDeleteMailRequest(IRequestBase request)
        => request is DeleteRequest
           || request is MoveRequest { ToFolder.SpecialFolderType: SpecialFolderType.Deleted };

    private static void PublishUndoableMailActionPackChanged(PendingUndoActionPack pack, UndoableMailActionPackState state)
    {
        if (pack.VisibleMailActionPack == null)
            return;

        WeakReferenceMessenger.Default.Send(new UndoableMailActionPackChanged(pack.VisibleMailActionPack, state));
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

    public async Task<ContactSynchronizationResult> SynchronizeContactsAsync(
        ContactSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var synchronizer = await GetOrCreateSynchronizerAsync(options.AccountId).ConfigureAwait(false);
        if (synchronizer is null)
            return ContactSynchronizationResult.Failed(new InvalidOperationException("Can't create/get synchronizer."));

        var accountCancellationSource = _accountSynchronizationCancellationSources.GetOrAdd(options.AccountId, _ => new CancellationTokenSource());
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accountCancellationSource.Token);

        try
        {
            PublishSynchronizationProgress(new AccountSynchronizationProgress(
                options.AccountId,
                SynchronizationProgressCategory.Contacts,
                true,
                true,
                0,
                0,
                0,
                "Synchronizing contacts...",
                AccountSynchronizerState.Synchronizing));

            var result = await synchronizer.SynchronizeContactsAsync(options, linkedSource.Token).ConfigureAwait(false);
            if (result.Exception is AuthenticationAttentionException authenticationException)
            {
                var account = authenticationException.Account ?? await _accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false);
                if (account is not null)
                {
                    account.IsContactReauthorizationRequired = true;
                    await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                }
            }
            return result;
        }
        catch (AuthenticationAttentionException ex)
        {
            var account = ex.Account ?? await _accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false);
            if (account is not null)
            {
                account.IsContactReauthorizationRequired = true;
                await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            }

            return ContactSynchronizationResult.Failed(ex);
        }
        catch (OperationCanceledException)
        {
            return ContactSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Contact synchronization failed for account {AccountId}.", options.AccountId);
            return ContactSynchronizationResult.Failed(ex);
        }
        finally
        {
            PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(options.AccountId, SynchronizationProgressCategory.Contacts));
        }
    }

    public async Task<TaskSynchronizationResult> SynchronizeTasksAsync(
        TaskSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (options is null)
            return TaskSynchronizationResult.Failed(new ArgumentNullException(nameof(options)));

        var synchronizer = await GetOrCreateSynchronizerAsync(options.AccountId).ConfigureAwait(false);
        if (synchronizer is null)
            return TaskSynchronizationResult.Failed(new InvalidOperationException("Can't create/get synchronizer."));

        var accountCancellationSource = _accountSynchronizationCancellationSources.GetOrAdd(options.AccountId, _ => new CancellationTokenSource());
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accountCancellationSource.Token);
        try
        {
            PublishSynchronizationProgress(new AccountSynchronizationProgress(
                options.AccountId,
                SynchronizationProgressCategory.Tasks,
                true,
                true,
                0,
                0,
                0,
                "Synchronizing tasks...",
                AccountSynchronizerState.Synchronizing));

            var result = await synchronizer.SynchronizeTasksAsync(options, linkedSource.Token).ConfigureAwait(false);
            if (result.Exception is AuthenticationAttentionException authenticationException)
            {
                var account = authenticationException.Account ?? await _accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false);
                if (account is not null)
                {
                    account.IsTaskReauthorizationRequired = true;
                    await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                }
            }
            return result;
        }
        catch (AuthenticationAttentionException ex)
        {
            var account = ex.Account ?? await _accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false);
            if (account is not null)
            {
                account.IsTaskReauthorizationRequired = true;
                await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            }
            return TaskSynchronizationResult.Failed(ex);
        }
        catch (OperationCanceledException)
        {
            return TaskSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Task synchronization failed for account {AccountId}.", options.AccountId);
            return TaskSynchronizationResult.Failed(ex);
        }
        finally
        {
            PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(options.AccountId, SynchronizationProgressCategory.Tasks));
        }
    }

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
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Contacts));
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Tasks));

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
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Contacts));
        PublishSynchronizationProgress(AccountSynchronizationProgress.Idle(accountId, SynchronizationProgressCategory.Tasks));
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

        if (!ShouldTrackSynchronizationTelemetry(result.CompletedState))
            return;

        var tags = CreateSynchronizationTelemetryProperties(
            synchronizer?.Account,
            "mail",
            options.Type.ToString(),
            result.CompletedState,
            duration);

        var context = new Dictionary<string, string>
        {
            ["successful_folder_count"] = result.SuccessfulFolderCount.ToString(),
            ["failed_folder_count"] = result.FailedFolderCount.ToString(),
            ["total_folder_count"] = result.FolderResults.Count.ToString(),
            ["downloaded_count_bucket"] = GetCountBucket(result.TotalDownloadedCount),
            ["updated_count_bucket"] = GetCountBucket(result.TotalUpdatedCount),
            ["deleted_count_bucket"] = GetCountBucket(result.TotalDeletedCount)
        };
        MoveDetailedServerPropertiesToContext(tags, context);

        var issues = result.AllIssues?.ToList();
        AddIssueTelemetry(tags, context, issues, result.Exception);
        TrackSynchronizationFailure(
            options.AccountId,
            tags,
            context,
            issues,
            result.Exception);
    }

    private void TrackCalendarSynchronizationSummary(
        CalendarSynchronizationOptions options,
        IWinoSynchronizerBase synchronizer,
        CalendarSynchronizationResult result,
        TimeSpan duration)
    {
        if (_telemetryService == null || options == null || result == null)
            return;

        if (!ShouldTrackSynchronizationTelemetry(result.CompletedState))
            return;

        var tags = CreateSynchronizationTelemetryProperties(
            synchronizer?.Account,
            "calendar",
            options.Type.ToString(),
            result.CompletedState,
            duration);

        var context = new Dictionary<string, string>
        {
            ["downloaded_count_bucket"] = GetCountBucket(result.DownloadedEvents?.Count() ?? 0)
        };
        MoveDetailedServerPropertiesToContext(tags, context);
        var issues = result.AllIssues?.ToList();
        AddIssueTelemetry(tags, context, issues, result.Exception);
        TrackSynchronizationFailure(
            options.AccountId,
            tags,
            context,
            issues,
            result.Exception);
    }

    private void TrackSynchronizationFailure(
        Guid accountId,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, string> context,
        IReadOnlyCollection<SynchronizationIssue> issues,
        Exception exception)
    {
        var firstIssue = issues?.FirstOrDefault();
        var issueCategory = firstIssue?.Category ?? SynchronizerErrorCategory.Unknown;
        var exceptionType = firstIssue?.ExceptionType
                            ?? exception?.GetType().Name
                            ?? "Unknown";
        var shouldCaptureStack = issueCategory is SynchronizerErrorCategory.Unknown
            or SynchronizerErrorCategory.Validation;
        var fingerprint = new[]
        {
            "sync_failure",
            tags.GetValueOrDefault("provider", "unknown"),
            tags.GetValueOrDefault("sync_area", "unknown"),
            tags.GetValueOrDefault("sync_type", "unknown"),
            issueCategory.ToString(),
            exceptionType
        };

        _telemetryService.TrackEvent(new WinoTelemetryEvent
        {
            Name = "sync_failure",
            Level = shouldCaptureStack ? WinoTelemetryLevel.Error : WinoTelemetryLevel.Warning,
            Exception = shouldCaptureStack ? exception : null,
            Tags = tags,
            Context = context,
            Fingerprint = fingerprint,
            DeduplicationKey = $"{accountId:N}:{string.Join(':', fingerprint)}",
            DeduplicationWindow = TimeSpan.FromMinutes(30)
        });
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
            ["event_kind"] = "sync_failure",
            ["feature"] = "sync",
            ["sync_area"] = syncArea,
            ["sync_type"] = syncType,
            ["state"] = completedState.ToString(),
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

        properties["duration_bucket"] = GetDurationBucket(duration);
        return properties;
    }

    private static void AddIssueTelemetry(
        IDictionary<string, string> tags,
        IDictionary<string, string> context,
        IReadOnlyCollection<SynchronizationIssue> issues,
        Exception exception)
    {
        var firstIssue = issues?.FirstOrDefault();

        context["issue_count"] = (issues?.Count ?? 0).ToString();

        if (firstIssue != null)
        {
            tags["issue_category"] = firstIssue.Category.ToString();
            tags["issue_severity"] = firstIssue.Severity.ToString();
            context["was_handled"] = firstIssue.WasHandled.ToString();
            context["can_continue_sync"] = firstIssue.CanContinueSync.ToString();
            context["is_entity_not_found"] = firstIssue.IsEntityNotFound.ToString();

            if (!string.IsNullOrWhiteSpace(firstIssue.ExceptionType))
                tags["exception_type"] = firstIssue.ExceptionType;
        }

        if (exception != null && !tags.ContainsKey("exception_type"))
            tags["exception_type"] = exception.GetType().Name;
    }

    private static void MoveDetailedServerPropertiesToContext(
        IDictionary<string, string> tags,
        IDictionary<string, string> context)
    {
        var detailedKeys = tags.Keys
            .Where(key => key.EndsWith("_host", StringComparison.Ordinal))
            .ToArray();

        foreach (var key in detailedKeys)
        {
            context[key] = tags[key];
            tags.Remove(key);
        }
    }

    public static bool ShouldTrackSynchronizationTelemetry(SynchronizationCompletedState completedState)
        => completedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.PartiallyCompleted;

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
           cachedAccount.IsCalendarAccessGranted != currentAccount.IsCalendarAccessGranted ||
           cachedAccount.IsContactAccessGranted != currentAccount.IsContactAccessGranted ||
           cachedAccount.IsTaskAccessGranted != currentAccount.IsTaskAccessGranted ||
           !ConnectionSettingsMatch(cachedAccount.ServerInformation, currentAccount.ServerInformation);

    private static bool ConnectionSettingsMatch(CustomServerInformation left, CustomServerInformation right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        return left.GetConnectionProperties().OrderBy(item => item.Key)
            .SequenceEqual(right.GetConnectionProperties().OrderBy(item => item.Key)) &&
               left.ConnectionPolicyVersion == right.ConnectionPolicyVersion &&
               left.IncomingServerUsername == right.IncomingServerUsername &&
               left.IncomingServerPassword == right.IncomingServerPassword &&
               left.OutgoingServerUsername == right.OutgoingServerUsername &&
               left.OutgoingServerPassword == right.OutgoingServerPassword;
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
        => await SetAttentionAsync(account, AccountAttentionReason.InvalidCredentials).ConfigureAwait(false);

    private async Task SetAttentionAsync(MailAccount account, AccountAttentionReason reason)
    {
        if (account == null || _accountService == null)
            return;

        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);

        if (persistedAccount == null)
            return;

        if (persistedAccount.AttentionReason == reason)
            return;

        persistedAccount.AttentionReason = reason;
        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
    }

    private async Task<bool> IsSynchronizationBlockedByAttentionAsync(Guid accountId)
    {
        if (_accountService == null)
            return false;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        return account?.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed;
    }

    private void PublishSynchronizationProgress(AccountSynchronizationProgress progress)
    {
        var normalized = progress.IsInProgress
            ? progress
            : AccountSynchronizationProgress.Idle(progress.AccountId, progress.Category);

        var cache = normalized.Category switch
        {
            SynchronizationProgressCategory.Calendar => _calendarSynchronizationProgress,
            SynchronizationProgressCategory.Contacts => _contactSynchronizationProgress,
            SynchronizationProgressCategory.Tasks => _taskSynchronizationProgress,
            _ => _mailSynchronizationProgress
        };

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

    private sealed class PendingUndoActionPack
    {
        public Guid Id { get; init; }
        public Dictionary<Guid, List<IRequestBase>> RequestsByAccount { get; init; } = [];
        public HashSet<Guid> RequiresFolderSynchronizationByAccount { get; init; } = [];
        public CancellationTokenSource CancellationTokenSource { get; } = new();
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public int IntervalInSeconds { get; init; }
        public UndoableMailActionPack VisibleMailActionPack { get; set; }
        public bool IsCompleted { get; set; }
    }

    private sealed record UndoActionSettings(
        string Title,
        string Message,
        InfoBarMessageType Severity,
        int IntervalInSeconds);
}
