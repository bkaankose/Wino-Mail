using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// Interactive OAuth authorization for the UI process. The background companion can only
/// refresh tokens silently; flows that may show a broker window or a browser (account
/// setup, capability changes, fixing invalid credentials) must run here. Tokens land in
/// the shared caches in the publisher folder, so the companion picks them up silently.
/// Mirrors the authorization logic of SynchronizationManager.HandleAuthorizationAsync.
/// </summary>
public static class InteractiveAuthHelper
{
    public static async Task<TokenInformationEx> AuthorizeAsync(IAuthenticationProvider authenticationProvider,
                                                                MailProviderType providerType,
                                                                MailAccount account = null,
                                                                bool proposeCopyAuthorizationURL = false,
                                                                bool forceInteractive = false)
    {
        var authenticator = authenticationProvider.GetAuthenticator(providerType);

        // Some users are having issues with Gmail authentication: their browsers may never
        // launch to complete it. Offer to copy the auth URL so they can finish manually.
        if (proposeCopyAuthorizationURL && authenticator is IGmailAuthenticator gmailAuthenticator)
        {
            gmailAuthenticator.ProposeCopyAuthURL = true;
        }

        if (account == null)
        {
            // Initial authentication: always interactive.
            return await authenticator.GenerateTokenInformationAsync(null).ConfigureAwait(false);
        }

        if (forceInteractive)
        {
            // Capability upgrades must force a fresh consent prompt so a cached token
            // cannot keep the old scopes.
            await authenticator.DeleteTokenInformationAsync(account).ConfigureAwait(false);
            return await authenticator.GenerateTokenInformationAsync(account).ConfigureAwait(false);
        }

        return await authenticator.GetTokenInformationAsync(account).ConfigureAwait(false);
    }
}
