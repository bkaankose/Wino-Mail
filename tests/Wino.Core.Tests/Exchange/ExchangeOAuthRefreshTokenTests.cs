using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Authentication.Exchange;
using Wino.Authentication.Oidc;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using FluentAssertions;
using Moq;
using Xunit;

namespace Wino.Core.Tests.Exchange;

/// <summary>
/// An issuer that rotates refresh tokens revokes the whole token family when a replaced token is
/// presented again. Several components hold their own copy of the account, so the token to spend
/// has to be the persisted one, never the one in whichever copy happens to be asking.
/// </summary>
public class ExchangeOAuthRefreshTokenTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Mock<IOidcTokenClient> _tokenClient = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IServiceProvider> _services = new();
    private readonly List<string> _presented = [];
    private string _persistedRefreshToken = "token-1";

    public ExchangeOAuthRefreshTokenTests()
    {
        _tokenClient.Setup(c => c.GetDiscoveryDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OidcDiscoveryDocument());

        // A rotating issuer: every refresh answers with the next token, and an access token that has already expired
        // so that the following call refreshes again.
        _tokenClient.Setup(c => c.RefreshAsync(It.IsAny<OidcDiscoveryDocument>(), It.IsAny<OidcConfiguration>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OidcDiscoveryDocument _, OidcConfiguration _, string presented, CancellationToken _) =>
            {
                _presented.Add(presented);
                return new OidcTokenSet
                {
                    AccessToken = $"access-{_presented.Count}",
                    RefreshToken = $"token-{_presented.Count + 1}",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                };
            });

        // The authenticator resolves the account service lazily, to stay out of a dependency cycle.
        _services.Setup(s => s.GetService(typeof(IAccountService))).Returns(_accounts.Object);

        _accounts.Setup(a => a.GetAccountAsync(_accountId)).ReturnsAsync(() => CreateAccountCopy(_persistedRefreshToken));
        _accounts.Setup(a => a.UpdateAccountCustomServerInformationAsync(It.IsAny<CustomServerInformation>()))
            .Callback((CustomServerInformation server) => _persistedRefreshToken = server.OAuthRefreshToken)
            .Returns(Task.CompletedTask);
    }

    private MailAccount CreateAccountCopy(string refreshToken) => new()
    {
        Id = _accountId,
        ServerInformation = new CustomServerInformation
        {
            AccountId = _accountId,
            UseOAuthAuthentication = true,
            OAuthAuthority = "https://sts.example.com/adfs",
            OAuthClientId = "client",
            OAuthResource = "https://mail.example.com/",
            OAuthRedirectUri = "https://mail.example.com/owa/",
            OAuthRefreshToken = refreshToken,
        },
    };

    [Fact]
    public async Task ASecondCopyOfTheAccount_SpendsTheRotatedToken_NotItsOwnStaleOne()
    {
        var cache = new ExchangeTokenCache();
        var authenticator = new ExchangeOAuthAuthenticator(_tokenClient.Object, _services.Object, cache);

        // Two components, each with its own copy, both loaded while "token-1" was current.
        var synchronizerCopy = CreateAccountCopy("token-1");
        var listenerCopy = CreateAccountCopy("token-1");

        await authenticator.TryGetBearerTokenAsync(synchronizerCopy);
        await authenticator.TryGetBearerTokenAsync(listenerCopy);

        _presented.Should().Equal("token-1", "token-2");
        _persistedRefreshToken.Should().Be("token-3");
        listenerCopy.ServerInformation.OAuthRefreshToken.Should().Be("token-3", "the copy that asked is brought up to date as well");
    }

    [Fact]
    public async Task ATokenStoredByANewSignIn_IsUsed_EvenThoughTheCallerStillHoldsTheRevokedOne()
    {
        var authenticator = new ExchangeOAuthAuthenticator(_tokenClient.Object, _services.Object, new ExchangeTokenCache());
        var staleCopy = CreateAccountCopy("token-1");

        // The user signed in again from account settings; only the database knows.
        _persistedRefreshToken = "token-from-new-sign-in";

        await authenticator.TryGetBearerTokenAsync(staleCopy);

        _presented.Should().Equal("token-from-new-sign-in");
    }
}
