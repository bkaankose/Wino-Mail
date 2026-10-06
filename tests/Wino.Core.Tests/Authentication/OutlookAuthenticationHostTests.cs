using Microsoft.Identity.Client;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class OutlookAuthenticationHostTests
{
    [Fact]
    public async Task Generate_WhenHostHasNoInteractiveContext_ReportsAttentionWithoutRequestingUi()
    {
        var host = CreateHost(interactive: false);
        var authenticator = new OutlookAuthenticator(host.Object, new MailAuthenticatorConfiguration());

        await Assert.ThrowsAsync<AuthenticationAttentionException>(() => authenticator.GenerateTokenInformationAsync(new MailAccount()));

        host.Verify(value => value.AcquireTokenInteractiveAsync(It.IsAny<IEnumerable<string>>(),
            It.IsAny<IAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubstrateSilent_WhenHostHasNoUiAndNoStoredAccount_ReturnsNullWithoutInteraction()
    {
        var host = CreateHost(interactive: false);
        var authenticator = new OutlookAuthenticator(host.Object, new MailAuthenticatorConfiguration());

        Assert.Null(await authenticator.GetSubstrateTaskTokenAsync(new MailAccount()));
        host.Verify(value => value.AcquireTokenInteractiveAsync(It.IsAny<IEnumerable<string>>(),
            It.IsAny<IAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Generate_WhenCallerCancels_PropagatesCancellationWithoutInteraction()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = CreateHost(interactive: true);
        host.Setup(value => value.EnsureTokenCacheAttachedAsync(cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var authenticator = new OutlookAuthenticator(host.Object, new MailAuthenticatorConfiguration());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authenticator.GenerateTokenInformationAsync(
            new MailAccount(), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Delete_RemovesOnlyLocalCacheAccountUsingAuthenticationAddress()
    {
        var host = CreateHost(interactive: false);
        var authenticator = new OutlookAuthenticator(host.Object, new MailAuthenticatorConfiguration());

        await authenticator.DeleteTokenInformationAsync(new MailAccount
        {
            Address = "mailbox@example.com",
            AuthenticationAddress = "upn@example.com",
        });

        host.Verify(value => value.RemoveLocalAccountAsync("upn@example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IOutlookAuthenticationHost> CreateHost(bool interactive)
    {
        var client = new Mock<IPublicClientApplication>();
        client.Setup(value => value.GetAccountsAsync()).ReturnsAsync(Array.Empty<IAccount>());
        var host = new Mock<IOutlookAuthenticationHost>();
        host.SetupGet(value => value.Client).Returns(client.Object);
        host.SetupGet(value => value.CanAuthenticateInteractively).Returns(interactive);
        host.Setup(value => value.EnsureTokenCacheAttachedAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        host.Setup(value => value.RemoveLocalAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return host;
    }
}
