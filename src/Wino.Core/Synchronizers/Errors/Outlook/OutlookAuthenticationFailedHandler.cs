using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Outlook;

public class OutlookAuthenticationFailedHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<OutlookAuthenticationFailedHandler>();
    private readonly IAccountService _accountService;

    public OutlookAuthenticationFailedHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.Exception is ApiException apiException)
        {
            if (apiException.ResponseStatusCode == 401)
                return true;

            if (apiException.ResponseStatusCode == 403)
            {
                var message = apiException.Message?.ToLowerInvariant() ?? string.Empty;
                return message.Contains("access denied") || message.Contains("authentication");
            }
        }

        if (error.Exception is ODataError oDataError)
        {
            if (oDataError.ResponseStatusCode == 401)
                return true;

            var code = oDataError.Error?.Code?.ToLowerInvariant() ?? string.Empty;
            var message = oDataError.Error?.Message?.ToLowerInvariant() ?? string.Empty;

            return code.Contains("invalidauthenticationtoken") ||
                   code.Contains("invalidgrant") ||
                   code.Contains("token") ||
                   message.Contains("access token") ||
                   message.Contains("authentication");
        }

        return false;
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Outlook authentication failed for account {AccountName} ({AccountId}). User intervention is required.",
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
