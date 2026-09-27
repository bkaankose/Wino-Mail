using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Repairs a Gmail or Outlook account that lost its credentials, for example an account
/// restored from a Wino Account backup, without re-creating it or touching its local data.
/// </summary>
public interface IAccountReauthenticationService
{
    /// <summary>
    /// Signs the account in interactively, requesting the scopes of exactly the modes the user
    /// enabled (calendar, contacts and To Do with a provider backend, plus active optional
    /// features), marks those modes granted, stores the new sign-in identity and clears the
    /// credential attention. Throws when the user cancels or authentication fails.
    /// </summary>
    /// <returns>The persisted account after the attention is cleared.</returns>
    Task<MailAccount> ReauthenticateAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the post-authentication work that account setup performs for a new account:
    /// profile (display name and picture), folders, categories and aliases for mail, then
    /// queues the initial mail, calendar, contact and task synchronizations for every mode
    /// the account has enabled and granted. Individual step failures are logged, not thrown.
    /// </summary>
    Task SynchronizeAfterReauthenticationAsync(Guid accountId, CancellationToken cancellationToken = default);
}
