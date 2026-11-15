#nullable enable
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.MAPI;

namespace Wino.Core.Synchronizers.Errors.Exchange;

/// <summary>Flags Exchange authentication failures (EWS and MAPI over HTTP) for account repair.</summary>
public partial class ExchangeAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public ExchangeAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.ErrorCode == 401
            || error.Exception is AuthenticationAttentionException
            || error.Exception is MapiTransportException { IsUnauthorized: true })
            return true;

        var message = error.ErrorMessage ?? error.Exception?.Message;
        if (string.IsNullOrEmpty(message))
            return false;

        // "401" must stand alone: a MAPI return value such as 0x8004011B contains those digits, and treating a
        // corrupt-data ROP failure as a credential failure would lock the account out of sync.
        return StandaloneUnauthorizedStatus().IsMatch(message)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Exchange authentication failed for account {AccountName} ({AccountId}). Re-authentication required.",
            error.Account?.Name, error.Account?.Id);

        if (error.Account != null)
            await PersistInvalidCredentialAttentionAsync(error.Account).ConfigureAwait(false);

        error.Severity = SynchronizerErrorSeverity.AuthRequired;
        error.Category = SynchronizerErrorCategory.Authentication;
        error.RetryDelay = null;

        return true;
    }

    private async Task PersistInvalidCredentialAttentionAsync(MailAccount account)
    {
        var persisted = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        if (persisted == null || persisted.AttentionReason == AccountAttentionReason.InvalidCredentials)
            return;

        persisted.AttentionReason = AccountAttentionReason.InvalidCredentials;
        await _accountService.UpdateAccountAsync(persisted).ConfigureAwait(false);
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-fx])401(?![0-9A-Fa-f])")]
    private static partial Regex StandaloneUnauthorizedStatus();
}
