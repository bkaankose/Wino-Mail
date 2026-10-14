#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// Handles data-only relay pushes. A <c>mailbox.changed</c> push (also the one after resume or unmute, <c>r = resync</c>)
/// runs one inbox-only sync for the account in <c>cid</c>. A wrong password is reported by the normal IMAP error handling.
/// </summary>
public sealed class PushRelayNotificationHandler : IPushRelayNotificationHandler
{
    internal static readonly TimeSpan SynchronizationBudget = TimeSpan.FromSeconds(25);

    private readonly IPushRelayRegistrationService _registration;
    private readonly IAccountService _accountService;
    private readonly Func<IApplicationRuntime> _runtime;
    private readonly Func<INotificationBuilder?>? _notifications;
    private readonly ILogger _logger = Log.ForContext<PushRelayNotificationHandler>();

    public PushRelayNotificationHandler(IPushRelayRegistrationService registration, IAccountService accountService, Func<IApplicationRuntime> runtime,
        Func<INotificationBuilder?>? notifications = null)
    {
        _registration = registration;
        _accountService = accountService;
        _runtime = runtime;
        _notifications = notifications;
    }

    public async Task<PushRelayNotificationOutcome> HandleAsync(IReadOnlyDictionary<string, string> payload, CancellationToken cancellationToken = default)
    {
        if (payload is null || !payload.TryGetValue(PushRelayPayloadKeys.Kind, out var kind) || string.IsNullOrWhiteSpace(kind))
            return PushRelayNotificationOutcome.Ignored;

        if (kind == PushRelayPushKind.Test)
        {
            _registration.ReportTestPush();
            // The user pressed "Send test notification"; show that it arrived.
            try
            {
                if (_notifications?.Invoke() is { } notifications)
                    await notifications.CreateLiveNotificationsTestNotificationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "The live notifications test notification could not be shown.");
            }

            return PushRelayNotificationOutcome.NoData;
        }

        if (kind is not (PushRelayPushKind.MailboxChanged or PushRelayPushKind.MailboxReauth or PushRelayPushKind.MailboxStatus
            or PushRelayPushKind.MailboxError or PushRelayPushKind.MailboxRenew))
        {
            return PushRelayNotificationOutcome.Ignored;
        }

        if (!payload.TryGetValue(PushRelayPayloadKeys.ClientAccountId, out var clientAccountId)
            || !Guid.TryParse(clientAccountId, out var accountId)
            || await _accountService.GetAccountAsync(accountId).ConfigureAwait(false) is null)
        {
            return PushRelayNotificationOutcome.Ignored;
        }

        var status = Read(payload, PushRelayPayloadKeys.Status);
        var reason = Read(payload, PushRelayPayloadKeys.MonitoringReason);
        var message = Read(payload, PushRelayPayloadKeys.MonitoringMessage);

        switch (kind)
        {
            case PushRelayPushKind.MailboxChanged:
                return await SynchronizeAsync(accountId, cancellationToken).ConfigureAwait(false);

            case PushRelayPushKind.MailboxReauth:
                _registration.ReportMailboxStatus(accountId, status ?? PushRelayMailboxStatuses.NeedsReauth, reason, message);
                _registration.RequestSync(PushRelaySyncReason.CredentialsChanged);
                return await SynchronizeAsync(accountId, cancellationToken).ConfigureAwait(false);

            case PushRelayPushKind.MailboxStatus:
            case PushRelayPushKind.MailboxError:
                _registration.ReportMailboxStatus(accountId, status, reason, message);
                return PushRelayNotificationOutcome.NoData;

            default:
                // mailbox.renew belongs to app-managed Gmail and Graph registrations, which come later.
                _logger.Information("Relay asked to renew a registration for account {AccountId}; not supported yet.", accountId);
                return PushRelayNotificationOutcome.NoData;
        }
    }

    private async Task<PushRelayNotificationOutcome> SynchronizeAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(SynchronizationBudget);
        try
        {
            var succeeded = await _runtime().SynchronizeAccountInboxAsync(accountId, bounded.Token).ConfigureAwait(false);
            return succeeded ? PushRelayNotificationOutcome.NewData : PushRelayNotificationOutcome.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Information("Push-triggered sync for account {AccountId} ran out of time.", accountId);
            return PushRelayNotificationOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Push-triggered sync for account {AccountId} failed.", accountId);
            return PushRelayNotificationOutcome.Failed;
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string> payload, string key)
        => payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
