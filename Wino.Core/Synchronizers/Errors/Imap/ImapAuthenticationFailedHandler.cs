using System.Threading.Tasks;
using MailKit.Security;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Imap;

/// <summary>
/// Handles IMAP authentication failures (AuthenticationException, SaslException).
/// Marks the error as requiring re-authentication.
/// </summary>
public class ImapAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ImapAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public ImapAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.Exception is AuthenticationException ||
               error.Exception is SaslException ||
               (error.ErrorMessage?.Contains("authentication", System.StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "IMAP authentication failed for account {AccountName} ({AccountId}). User needs to re-authenticate.",
            error.Account?.Name, error.Account?.Id);

        if (error.Account != null)
        {
            await PersistInvalidCredentialAttentionAsync(error.Account).ConfigureAwait(false);
        }

        // Mark as requiring authentication - this will stop sync and notify user
        error.Severity = SynchronizerErrorSeverity.AuthRequired;
        error.Category = SynchronizerErrorCategory.Authentication;

        // No point in retrying auth failures - credentials need to be updated
        error.RetryDelay = null;

        return true;
    }

    private async Task PersistInvalidCredentialAttentionAsync(MailAccount account)
    {
        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);

        if (persistedAccount == null)
            return;

        if (persistedAccount.AttentionReason == AccountAttentionReason.InvalidCredentials)
            return;

        persistedAccount.AttentionReason = AccountAttentionReason.InvalidCredentials;
        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
    }
}
