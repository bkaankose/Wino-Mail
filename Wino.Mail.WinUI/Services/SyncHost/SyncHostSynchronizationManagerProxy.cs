using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.SyncHost;

namespace Wino.Mail.WinUI.Services.SyncHost;

public sealed class SyncHostSynchronizationManagerProxy : IRemoteSynchronizationManager
{
    private readonly SyncHostPipeClient _pipeClient;
    private readonly IAccountService _accountService;
    private readonly ILogger _logger = Log.ForContext<SyncHostSynchronizationManagerProxy>();

    public SyncHostSynchronizationManagerProxy(
        SyncHostPipeClient pipeClient,
        IAccountService accountService)
    {
        _pipeClient = pipeClient;
        _accountService = accountService;
    }

    public static SerializedRequestPayload SerializeRequest(IRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var typeName = requestType.AssemblyQualifiedName
                       ?? throw new InvalidOperationException($"Request type {requestType.FullName} has no assembly qualified name.");
        var json = JsonSerializer.Serialize(request, requestType, SyncHostJson.Options);

        return new SerializedRequestPayload(typeName, json);
    }

    public Task InitializeAsync(
        ISynchronizerFactory synchronizerFactory,
        IImapTestService imapTestService,
        IAccountService accountService,
        INotificationBuilder notificationBuilder,
        IAuthenticationProvider authenticationProvider)
        => Task.CompletedTask;

    public Task<ImapConnectivityTestResults> TestImapConnectivityAsync(CustomServerInformation serverInformation, bool allowSSLHandshake)
        => Task.FromException<ImapConnectivityTestResults>(
            new NotSupportedException("IMAP connectivity validation is handled by the UI synchronization manager."));

    public Task<MailSynchronizationResult> SynchronizeMailAsync(
        MailSynchronizationOptions options,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync<MailSynchronizationOptions, MailSynchronizationResult>(
            SyncHostProtocol.Commands.SynchronizeMail,
            options,
            cancellationToken)!;

    public bool IsAccountSynchronizing(Guid accountId)
    {
        try
        {
            return _pipeClient
                .SendAsync<AccountIdPayload, bool>(
                    SyncHostProtocol.Commands.IsAccountSynchronizing,
                    new AccountIdPayload(accountId))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to query background sync state for account {AccountId}.", accountId);
            return false;
        }
    }

    public AccountSynchronizationProgress GetSynchronizationProgress(
        Guid accountId,
        SynchronizationProgressCategory category)
    {
        try
        {
            return _pipeClient
                .SendAsync<SynchronizationProgressRequestPayload, AccountSynchronizationProgress>(
                    SyncHostProtocol.Commands.GetSynchronizationProgress,
                    new SynchronizationProgressRequestPayload(accountId, category))
                .GetAwaiter()
                .GetResult()
                ?? AccountSynchronizationProgress.Idle(accountId, category);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to query background sync progress for account {AccountId}.", accountId);
            return AccountSynchronizationProgress.Idle(accountId, category);
        }
    }

    public Task QueueRequestAsync(IRequestBase request, Guid accountId, bool triggerSynchronization)
        => QueueRequestsAsync([request], accountId, triggerSynchronization);

    public Task QueueRequestsAsync(IEnumerable<IRequestBase> requests, Guid accountId, bool triggerSynchronization)
    {
        var serializedRequests = requests
            .Where(request => request != null)
            .Select(SerializeRequest)
            .ToList();

        if (serializedRequests.Count == 0)
            return Task.CompletedTask;

        return _pipeClient.SendAsync(
            SyncHostProtocol.Commands.QueueRequests,
            new QueueRequestsPayload(accountId, triggerSynchronization, serializedRequests));
    }

    public Task<MailSynchronizationResult> SynchronizeFoldersAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => SynchronizeMailAsync(
            new MailSynchronizationOptions { AccountId = accountId, Type = MailSynchronizationType.FoldersOnly },
            cancellationToken);

    public Task<MailSynchronizationResult> SynchronizeAliasesAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => SynchronizeMailAsync(
            new MailSynchronizationOptions { AccountId = accountId, Type = MailSynchronizationType.Alias },
            cancellationToken);

    public Task<MailSynchronizationResult> SynchronizeCategoriesAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => SynchronizeMailAsync(
            new MailSynchronizationOptions { AccountId = accountId, Type = MailSynchronizationType.Categories },
            cancellationToken);

    public Task<MailSynchronizationResult> SynchronizeProfileAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => SynchronizeMailAsync(
            new MailSynchronizationOptions { AccountId = accountId, Type = MailSynchronizationType.UpdateProfile },
            cancellationToken);

    public Task<CalendarSynchronizationResult> SynchronizeCalendarAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync<CalendarSynchronizationOptions, CalendarSynchronizationResult>(
            SyncHostProtocol.Commands.SynchronizeCalendar,
            options,
            cancellationToken)!;

    public Task<string> DownloadMimeMessageAsync(
        MailCopy mailItem,
        Guid accountId,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync<DownloadMimeMessagePayload, string>(
            SyncHostProtocol.Commands.DownloadMimeMessage,
            new DownloadMimeMessagePayload(mailItem, accountId),
            cancellationToken)!;

    public Task DownloadCalendarAttachmentAsync(
        CalendarItem calendarItem,
        CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.DownloadCalendarAttachment,
            new DownloadCalendarAttachmentPayload(calendarItem, attachment, localFilePath),
            cancellationToken);

    public IWinoSynchronizerBase CreateSynchronizerForAccount(MailAccount account)
        => account == null ? null : new RemoteWinoSynchronizer(account, _pipeClient);

    public Task CancelSynchronizationsAsync(Guid accountId)
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.CancelSynchronizations,
            new AccountIdPayload(accountId));

    public Task DestroySynchronizerAsync(Guid accountId)
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.DestroySynchronizer,
            new AccountIdPayload(accountId));

    public IEnumerable<IWinoSynchronizerBase> GetAllSynchronizers()
        => [];

    public async Task<IWinoSynchronizerBase> GetSynchronizerAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        return account == null ? null : new RemoteWinoSynchronizer(account, _pipeClient);
    }

    public Task<TokenInformationEx> HandleAuthorizationAsync(
        MailProviderType providerType,
        MailAccount account = null,
        bool proposeCopyAuthorizationURL = false,
        bool forceInteractive = false)
        => Task.FromException<TokenInformationEx>(
            new NotSupportedException("Interactive authorization is handled by the UI synchronization manager."));

    public Task ShutdownHostAsync()
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.ShutdownHost,
            new object());
}
