using System.Web;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GmailAuthenticatorPresenterTests
{
    [Fact]
    public async Task GenerateTokenInformationAsync_CancelFromPresenter_ThrowsCanceledAndClosesSession()
    {
        var nativeAppService = new Mock<INativeAppService>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>())).ReturnsAsync(true);

        var presenter = new FakePresenter(onShown: session => session.CancelRequested());
        var authenticator = CreateAuthenticator(nativeAppService.Object, presenter);

        await Assert.ThrowsAsync<AccountSetupCanceledException>(
            () => authenticator.GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));

        Assert.NotNull(presenter.Session);
        Assert.Equal("Google", presenter.Session!.Request.ProviderDisplayName);
        Assert.True(presenter.Session.IsDisposed);
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_BrowserLaunchFails_ReportsToSessionAndKeepsWaitingForRedirect()
    {
        using var httpClient = new HttpClient();
        var nativeAppService = new Mock<INativeAppService>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>())).ReturnsAsync(false);

        Task<string>? browserResponse = null;
        var presenter = new FakePresenter(onLaunchFailed: session =>
        {
            // The user copied the address and finished (here: denied) the sign-in by hand.
            var query = HttpUtility.ParseQueryString(session.Request.AuthorizationUri.Query);
            var redirectUri = query["redirect_uri"]!;
            var state = Uri.EscapeDataString(query["state"]!);
            browserResponse = httpClient.GetStringAsync($"{redirectUri}?error=access_denied&state={state}");
        });
        var authenticator = CreateAuthenticator(nativeAppService.Object, presenter);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));

        Assert.Contains("access_denied", exception.Message);
        Assert.True(presenter.Session!.LaunchFailureReported);
        Assert.True(presenter.Session.RedirectReported);
        Assert.True(presenter.Session.IsDisposed);
        Assert.Contains("Authorization failed", await browserResponse!);
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_BrowserLaunchFailsWithoutPresenter_Throws()
    {
        var nativeAppService = new Mock<INativeAppService>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>())).ReturnsAsync(false);

        var authenticator = CreateAuthenticator(nativeAppService.Object, presenter: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));

        Assert.Contains("browser could not be opened", exception.Message);
    }

    private static GmailAuthenticator CreateAuthenticator(INativeAppService nativeAppService, IExternalBrowserAuthenticationPresenter? presenter)
    {
        var configuration = new MailAuthenticatorConfiguration(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = Path.GetTempPath()
        });

        return new GmailAuthenticator(configuration, nativeAppService, presenter);
    }

    private sealed class FakePresenter(
        Action<FakeSession>? onShown = null,
        Action<FakeSession>? onLaunchFailed = null) : IExternalBrowserAuthenticationPresenter
    {
        public FakeSession? Session { get; private set; }

        public Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
        {
            Session = new FakeSession(request, cancelRequested, onLaunchFailed);
            onShown?.Invoke(Session);

            return Task.FromResult<IExternalBrowserAuthenticationSession>(Session);
        }
    }

    private sealed class FakeSession(
        ExternalBrowserAuthenticationRequest request,
        Action cancelRequested,
        Action<FakeSession>? onLaunchFailed) : IExternalBrowserAuthenticationSession
    {
        public ExternalBrowserAuthenticationRequest Request { get; } = request;
        public Action CancelRequested { get; } = cancelRequested;
        public bool LaunchFailureReported { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool RedirectReported { get; private set; }

        public Task NotifyBrowserLaunchFailedAsync()
        {
            LaunchFailureReported = true;
            onLaunchFailed?.Invoke(this);

            return Task.CompletedTask;
        }

        public Task NotifyRedirectReceivedAsync()
        {
            RedirectReported = true;

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
