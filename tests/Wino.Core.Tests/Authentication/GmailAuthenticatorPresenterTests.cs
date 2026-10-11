using System.Web;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Platform;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GmailAuthenticatorPresenterTests
{
    [Fact]
    public async Task GenerateTokenInformationAsync_CancelFromPresenter_ThrowsCanceledAndClosesSession()
    {
        var nativeAppService = new Mock<IExternalLauncher>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Succeeded));

        var presenter = new FakePresenter(onShown: session => session.CancelRequested());
        var authenticator = CreateAuthenticator(nativeAppService.Object, presenter);

        await Assert.ThrowsAsync<AccountSetupCanceledException>(
            () => authenticator.GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));

        Assert.NotNull(presenter.Session);
        Assert.Equal("Google", presenter.Session!.Request.ProviderDisplayName);
        Assert.True(presenter.Session.IsDisposed);
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_AuthorizationAddress_IsFullyEscapedAndRoundTrips()
    {
        Uri? launchedUri = null;
        var launcher = new Mock<IExternalLauncher>();
        launcher.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Callback<Uri, CancellationToken>((uri, _) => launchedUri = uri)
            .ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
        var presenter = new FakePresenter(onShown: session => session.CancelRequested());
        var authenticator = CreateAuthenticator(launcher.Object, presenter);
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true
        };

        await Assert.ThrowsAsync<AccountSetupCanceledException>(() => authenticator.GenerateTokenInformationAsync(account));

        var uri = presenter.Session!.Request.AuthorizationUri;
        Assert.Same(uri, launchedUri);

        // The launcher and the copy button both rely on the escaped form; it must already be canonical.
        Assert.Equal(uri.AbsoluteUri, uri.OriginalString);
        Assert.DoesNotContain(" ", uri.AbsoluteUri);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", uri.GetLeftPart(UriPartial.Path));

        var query = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in new[] { "client_id", "redirect_uri", "response_type", "scope", "access_type", "prompt", "state", "code_challenge", "code_challenge_method" })
            Assert.Single(query.GetValues(key) ?? []);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Matches("^http://127\\.0\\.0\\.1:\\d+/authorize/$", query["redirect_uri"]);

        var scopes = query["scope"]!.Split(' ');
        Assert.Contains("https://mail.google.com/", scopes);
        Assert.Contains("https://www.googleapis.com/auth/calendar", scopes);
        Assert.All(scopes, scope => Assert.True(Uri.IsWellFormedUriString(scope, UriKind.Absolute), scope));
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_Cancelled_DoesNotLeaveAnUnobservedListenerFailure()
    {
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.Flatten().InnerExceptions.Any(ex => ex is System.Net.HttpListenerException or ObjectDisposedException))
                lock (unobserved) unobserved.Add(args.Exception);
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await RunCancelledSignInAsync();

            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Empty(unobserved);
    }

    private static async Task RunCancelledSignInAsync()
    {
        var launcher = new Mock<IExternalLauncher>();
        launcher.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
        var presenter = new FakePresenter(onShown: session => session.CancelRequested());

        await Assert.ThrowsAsync<AccountSetupCanceledException>(
            () => CreateAuthenticator(launcher.Object, presenter).GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_CallerCancellation_PropagatesAndClosesSession()
    {
        using var cancellation = new CancellationTokenSource();
        var launcher = new Mock<IExternalLauncher>();
        launcher.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
        var presenter = new FakePresenter(onShown: _ => cancellation.Cancel());
        var authenticator = CreateAuthenticator(launcher.Object, presenter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authenticator.GenerateTokenInformationAsync(
            new MailAccount { Id = Guid.NewGuid() }, cancellationToken: cancellation.Token));

        Assert.True(presenter.Session!.IsDisposed);
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_BrowserLaunchFails_ReportsToSessionAndKeepsWaitingForRedirect()
    {
        using var httpClient = new HttpClient();
        var nativeAppService = new Mock<IExternalLauncher>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Failed, "The default browser could not be opened."));

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
        var nativeAppService = new Mock<IExternalLauncher>();
        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Failed, "The default browser could not be opened."));

        var authenticator = CreateAuthenticator(nativeAppService.Object, presenter: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GenerateTokenInformationAsync(new MailAccount { Id = Guid.NewGuid() }));

        Assert.Contains("browser could not be opened", exception.Message);
    }

    private static GmailAuthenticator CreateAuthenticator(IExternalLauncher nativeAppService, IExternalBrowserAuthenticationPresenter? presenter)
    {
        var configuration = new MailAuthenticatorConfiguration(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = Path.GetTempPath()
        });

        return new GmailAuthenticator(configuration, nativeAppService, presenter, new TestGoogleTokenStore());
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
