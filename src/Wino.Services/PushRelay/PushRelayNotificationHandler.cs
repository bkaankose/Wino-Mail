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
    private readonly IPushRelayAppManagedRegistrationStore? _appManaged;
    private readonly IRelayAlertCoverage? _coverage;
    private readonly bool _pushesAreAlerts;
    private readonly bool _boundSync;
    private readonly ILogger _logger = Log.ForContext<PushRelayNotificationHandler>();

    /// <summary>The relay's alert stays visible for about this long before the app may post its own notifications again.</summary>
    internal static readonly TimeSpan AlertCoverage = TimeSpan.FromSeconds(90);

    public PushRelayNotificationHandler(IPushRelayRegistrationService registration, IAccountService accountService, Func<IApplicationRuntime> runtime,
        Func<INotificationBuilder?>? notifications = null, IPushRelayAppManagedRegistrationStore? appManaged = null,
        IRelayAlertCoverage? coverage = null, IPushRelayDeviceInfo? deviceInfo = null)
    {
        _registration = registration;
        _accountService = accountService;
        _runtime = runtime;
        _notifications = notifications;
        _appManaged = appManaged;
        _coverage = coverage;
        // iOS: the relay sends a visible alert that the Notification Service Extension fills in. Everywhere else the app notifies.
        var platform = deviceInfo?.Describe().Platform;
        _pushesAreAlerts = platform == PushRelayPlatforms.Ios;
        // Phones get a short background window per push. The desktop socket keeps running, so its sync is never cut short.
        _boundSync = platform is not (PushRelayPlatforms.Windows or PushRelayPlatforms.MacOS);
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
                if (_pushesAreAlerts && _coverage is not null && Read(payload, PushRelayPayloadKeys.Reason) is "new_mail" or "webhook" or null)
                    _coverage.BeginCoverage(accountId, AlertCoverage);
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
                // mailbox.renew: the Gmail watch or Graph subscription is about to expire. The next pass renews it and posts it again.
                _appManaged?.MarkRenewalDue(accountId);
                _registration.RequestSync(PushRelaySyncReason.CredentialsChanged);
                return PushRelayNotificationOutcome.NoData;
        }
    }

    private async Task<PushRelayNotificationOutcome> SynchronizeAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_boundSync)
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
