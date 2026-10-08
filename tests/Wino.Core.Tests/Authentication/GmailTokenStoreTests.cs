using System.Text;
using System.Text.Json;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GmailTokenStoreTests
{
    [Fact]
    public async Task CachedToken_PreservesGeneratedRepresentationAndClearsReadBuffer()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "cached@example.com" };
        var bytes = Encoding.UTF8.GetBytes("""
            {"AccessToken":"cached-token","RefreshToken":"refresh-token","ExpiresAtUtc":"2099-01-01T00:00:00Z","Scopes":["mail"]}
            """);
        var store = new Mock<IGoogleTokenStore>();
        store.Setup(value => value.ReadAsync(account.Id.ToString("N"), It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        var authenticator = CreateAuthenticator(store.Object);

        var token = await authenticator.GetTokenInformationAsync(account);

        Assert.Equal("cached-token", token.AccessToken);
        Assert.All(bytes, value => Assert.Equal(0, value));
        store.Verify(value => value.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CorruptToken_PropagatesErrorAndDoesNotDeleteStoredCredential()
    {
        var bytes = Encoding.UTF8.GetBytes("broken-json");
        var store = new Mock<IGoogleTokenStore>();
        store.Setup(value => value.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        var authenticator = CreateAuthenticator(store.Object);

        await Assert.ThrowsAsync<JsonException>(() => authenticator.GetTokenInformationAsync(new MailAccount()));

        Assert.All(bytes, value => Assert.Equal(0, value));
        store.Verify(value => value.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingToken_ReportsAuthenticationAttention()
    {
        var store = new Mock<IGoogleTokenStore>();
        store.Setup(value => value.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[])null!);

        await Assert.ThrowsAsync<AuthenticationAttentionException>(() => CreateAuthenticator(store.Object)
            .GetTokenInformationAsync(new MailAccount()));
    }

    private static GmailAuthenticator CreateAuthenticator(IGoogleTokenStore store)
        => new(new MailAuthenticatorConfiguration(), Mock.Of<IExternalLauncher>(), null, store);
}
