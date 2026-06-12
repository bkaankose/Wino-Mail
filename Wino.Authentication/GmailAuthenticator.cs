using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication;

public class GmailAuthenticator : BaseAuthenticator, IGmailAuthenticator
{
    public GmailAuthenticator(IAuthenticatorConfig authConfig) : base(authConfig)
    {
    }

    public string ClientId => AuthenticatorConfig.GmailAuthenticatorClientId;
    public bool ProposeCopyAuthURL { get; set; }

    public override MailProviderType ProviderType => MailProviderType.Gmail;

    /// <summary>
    /// Generates the token information for the given account.
    /// For Gmail, remove the stored credential first so capability changes request
    /// a token with the current account scopes instead of reusing an older grant.
    /// </summary>
    /// <param name="account">Account to get token for.</param>
    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
    {
        await DeleteTokenInformationAsync(account).ConfigureAwait(false);
        return await GetTokenInformationAsync(account).ConfigureAwait(false);
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account)
    {
        var userCredential = await GetGoogleUserCredentialAsync(account);

        if (userCredential.Token.IsStale)
        {
            await userCredential.RefreshTokenAsync(CancellationToken.None);
        }

        return new TokenInformationEx(userCredential.Token.AccessToken, account?.Address);
    }

    private Task<UserCredential> GetGoogleUserCredentialAsync(MailAccount account)
    {
        return GoogleWebAuthorizationBroker.AuthorizeAsync(new ClientSecrets()
        {
            ClientId = ClientId
        }, AuthenticatorConfig.GetGmailScope(account?.IsMailAccessGranted != false, account?.IsCalendarAccessGranted == true), GetCredentialKey(account), CancellationToken.None, new FileDataStore(AuthenticatorConfig.GmailTokenStoreIdentifier));
    }

    public Task DeleteTokenInformationAsync(MailAccount account)
        => new FileDataStore(AuthenticatorConfig.GmailTokenStoreIdentifier)
            .DeleteAsync<TokenResponse>(GetCredentialKey(account));

    private static string GetCredentialKey(MailAccount account)
        => account?.Id.ToString() ?? "default";
}
