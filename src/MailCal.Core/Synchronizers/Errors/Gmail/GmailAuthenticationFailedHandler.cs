using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Google;
using Serilog;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Core.Synchronizers.Errors.Gmail;

public class GmailAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<GmailAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public GmailAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
        => IsAuthenticationFailure(error.Exception);

    /// <summary>
    /// True when Google rejected the credentials or the token lacks a scope the call needs.
    /// Both are fixed only by signing in again, so retrying the sync never helps.
    /// </summary>
    public static bool IsAuthenticationFailure(System.Exception exception)
    {
        if (exception is not GoogleApiException googleEx)
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
