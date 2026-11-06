#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Saves a new account and runs its first synchronization steps: sign-in (unless already done), save, profile,
/// folders, categories, calendar metadata, aliases, finalizing.
/// </summary>
public interface IAccountSetupRunner
{
    /// <summary>The steps a run of <paramref name="plan"/> goes through, in display order.</summary>
    IReadOnlyList<AccountSetupStepDescriptor> GetSteps(AccountSetupPlan plan);

    /// <summary>
    /// The unsaved account a native sign-in runs against before Add account: a new id, the typed address as the login
    /// hint, the Outlook client id, and the access flags that decide the requested scopes.
    /// </summary>
    MailAccount CreateTransientAccount(AccountSetupPlan plan);

    /// <summary>Signs a transient account in with Google or Microsoft. Throws <see cref="Exceptions.AccountSetupCanceledException"/> when the user cancels.</summary>
    Task<TokenInformationEx> SignInTransientAccountAsync(MailAccount transientAccount, CancellationToken cancellationToken = default);

    /// <summary>
    /// The mailbox address a native sign-in belongs to: the address Microsoft returned, or Google's userinfo address
    /// (Gmail tokens carry only the typed address). Throws when the provider does not return one, so nothing is saved
    /// for an account whose address is unknown.
    /// </summary>
    Task<string> ConfirmTransientAddressAsync(MailAccount transientAccount, TokenInformationEx tokenInformation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a native sign-in that will not be saved: deletes the Gmail token stored under the transient id. The
    /// MSAL cache keeps a Microsoft account entry, as an abandoned sign-in always did.
    /// </summary>
    Task DiscardTransientSignInAsync(MailAccount transientAccount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the plan. Step changes go to <paramref name="progress"/>; a server certificate that needs the user's trust
    /// goes to <paramref name="confirmCertificateTrust"/> (message, certificate bytes) and fails the step when it is null.
    /// Never throws for setup failures; the result carries them. Continues on the caller's context.
    /// </summary>
    Task<AccountSetupRunResult> RunAsync(
        AccountSetupPlan plan,
        IProgress<AccountSetupStepUpdate>? progress = null,
        Func<string, byte[], Task<bool>>? confirmCertificateTrust = null,
        CancellationToken cancellationToken = default);
}
