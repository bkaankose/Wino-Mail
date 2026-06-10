using System;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Exchange;

/// <summary>Flags Exchange authentication failures for account repair.</summary>
public class ExchangeAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public ExchangeAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.ErrorCode == 401)
            return true;

        var message = error.ErrorMessage ?? error.Exception?.Message;
        if (string.IsNullOrEmpty(message))
            return false;

        return message.Contains("401", StringComparison.OrdinalIgnoreCase)
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
        error.RetryDelay = null; // credentials must be fixed; retrying won't help

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
}
