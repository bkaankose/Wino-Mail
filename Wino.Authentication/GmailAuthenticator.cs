using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
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
    /// For gmail, interactivity is automatically handled when you get the token.
    /// </summary>
    /// <param name="account">Account to get token for.</param>
    public Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
        => GetTokenInformationAsync(account);

    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account)
    {
        var userCredential = await GetGoogleUserCredentialAsync(account);

        if (userCredential.Token.IsStale)
        {
            await userCredential.RefreshTokenAsync(CancellationToken.None);
        }

        return new TokenInformationEx(userCredential.Token.AccessToken, account.Address);
    }

    private Task<UserCredential> GetGoogleUserCredentialAsync(MailAccount account)
    {
        return GoogleWebAuthorizationBroker.AuthorizeAsync(new ClientSecrets()
        {
            ClientId = ClientId
        }, AuthenticatorConfig.GmailScope, account.Id.ToString(), CancellationToken.None, new FileDataStore(AuthenticatorConfig.GmailTokenStoreIdentifier));
    }
}
