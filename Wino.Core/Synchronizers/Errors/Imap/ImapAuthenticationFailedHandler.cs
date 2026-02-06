using System.Threading.Tasks;
using MailKit.Security;
using Serilog;
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

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.Exception is AuthenticationException ||
               error.Exception is SaslException ||
               (error.ErrorMessage?.Contains("authentication", System.StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "IMAP authentication failed for account {AccountName} ({AccountId}). User needs to re-authenticate.",
            error.Account?.Name, error.Account?.Id);

        // Mark as requiring authentication - this will stop sync and notify user
        error.Severity = SynchronizerErrorSeverity.AuthRequired;
        error.Category = SynchronizerErrorCategory.Authentication;

        // No point in retrying auth failures - credentials need to be updated
        error.RetryDelay = null;

        return Task.FromResult(true);
    }
}
