#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Supplies the token an account needs to receive mail moved from another account, when that token is for a different
/// resource than the account's synchronization token (Outlook: Exchange Online IMAP next to Graph). MSAL issues one
/// token per resource, so it is acquired on its own and never folded into the synchronization scopes.
/// </summary>
public interface IMailImportTokenProvider
{
    /// <summary>A token from the cache or a silent refresh; null when the user has not consented.</summary>
    Task<string?> GetMailImportTokenAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Silently first, then interactively: asks the user to consent to the import resource.
    /// Throws <see cref="Exceptions.AccountSetupCanceledException"/> when the user cancels or declines.
    /// </summary>
    Task AuthorizeMailImportAsync(MailAccount account, CancellationToken cancellationToken = default);
}
