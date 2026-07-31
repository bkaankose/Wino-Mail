using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Google;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Gmail;

public class GmailAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<GmailAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public GmailAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.Exception is not GoogleApiException googleEx)
            return false;

        var reason = googleEx.Error?.Errors?.FirstOrDefault()?.Reason?.ToLowerInvariant() ?? string.Empty;
        var message = googleEx.Message?.ToLowerInvariant() ?? string.Empty;

        return googleEx.HttpStatusCode == HttpStatusCode.Unauthorized ||
               (googleEx.HttpStatusCode == HttpStatusCode.Forbidden &&
                (reason.Contains("auth") ||
                 reason.Contains("credential") ||
                 message.Contains("invalid credentials") ||
                 message.Contains("insufficient authentication") ||
                 message.Contains("login required")));
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Gmail authentication failed for account {AccountName} ({AccountId}). User intervention is required.",
            error.Account?.Name, error.Account?.Id);

        if (error.Account != null)
        {
            await PersistInvalidCredentialAttentionAsync(error.Account).ConfigureAwait(false);
        }

        error.Severity = SynchronizerErrorSeverity.AuthRequired;
        error.Category = SynchronizerErrorCategory.Authentication;
        error.RetryDelay = null;

        return true;
    }

    private async Task PersistInvalidCredentialAttentionAsync(MailAccount account)
    {
        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);

        if (persistedAccount == null || persistedAccount.AttentionReason == AccountAttentionReason.InvalidCredentials)
            return;

        persistedAccount.AttentionReason = AccountAttentionReason.InvalidCredentials;
        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
    }
}
