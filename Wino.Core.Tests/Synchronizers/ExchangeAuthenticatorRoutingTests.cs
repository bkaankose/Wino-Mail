using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Synchronizers.Exchange;
using Xunit;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ExchangeAuthenticatorRoutingTests
{
    private static MailAccount AccountWith(CustomServerInformation server)
        => new() { Id = Guid.NewGuid(), ServerInformation = server };

    // OAuth authenticator deps are unused on the paths exercised here (NTLM dispatch, and the
    // no-refresh-token guard which throws before any network/persistence call).
    private static ExchangeAuthenticator CreateRouter()
        => new(new ExchangeNtlmAuthenticator(), new ExchangeOAuthAuthenticator(null, null, new ExchangeTokenCache()));

    [Fact]
    public async Task Router_UsesNtlm_WhenOAuthDisabled()
    {
        var account = AccountWith(new CustomServerInformation
        {
            UseOAuthAuthentication = false,
            IncomingServerUsername = "user@contoso.com",
            IncomingServerPassword = "pw",
        });

        var credentials = await CreateRouter().GetCredentialsAsync(account);

        credentials.Should().BeOfType<WebCredentials>();
    }

    [Fact]
    public async Task Router_UsesOAuth_AndRequiresSignIn_WhenNoRefreshToken()
    {
        var account = AccountWith(new CustomServerInformation
        {
            UseOAuthAuthentication = true,
            OAuthAuthority = "https://wsfed.example.com/adfs",
            OAuthClientId = "client",
            OAuthResource = "https://mail.example.com/",
            OAuthRefreshToken = null,
        });

        var act = async () => await CreateRouter().GetCredentialsAsync(account);

        await act.Should().ThrowAsync<ExchangeInteractiveSignInRequiredException>();
    }

    [Fact]
    public async Task Router_Throws_WhenServerInformationMissing()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), ServerInformation = null };

        var act = async () => await CreateRouter().GetCredentialsAsync(account);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void BuildConfiguration_MapsServerFields()
    {
        var server = new CustomServerInformation
        {
            OAuthAuthority = "https://wsfed.example.com/adfs",
            OAuthClientId = "00000002-0000-0ff1-ce00-000000000000",
            OAuthResource = "https://mail.example.com/",
            OAuthRedirectUri = "https://mail.example.com/owa/",
        };

        var config = ExchangeOAuthAuthenticator.BuildConfiguration(server);

        config.Authority.Should().Be(server.OAuthAuthority);
        config.ClientId.Should().Be(server.OAuthClientId);
        config.Resource.Should().Be(server.OAuthResource);
        config.RedirectUri.Should().Be(server.OAuthRedirectUri);
    }
}
