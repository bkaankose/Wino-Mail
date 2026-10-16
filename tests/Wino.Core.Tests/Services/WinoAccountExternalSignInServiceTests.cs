using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class WinoAccountExternalSignInServiceTests
{
    private static readonly Uri AuthorizeUri = new("https://api.example.test/api/v1/auth/apple/authorize");

    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private readonly Mock<IWinoAccountProfileService> _profile = new();

    public WinoAccountExternalSignInServiceTests()
        => _apiClient.Setup(x => x.GetExternalSignInAuthorizationUri(WinoAccountExternalAuth.AppleProvider)).Returns(AuthorizeUri);

    [Fact]
    public async Task SignInWithAppleAsync_CompletesWithTheCallbackCode()
    {
        var account = new WinoAccount { Id = Guid.NewGuid(), Email = "user@example.test" };
        var broker = new FakeBroker(_ => Task.FromResult(new Uri("wino-mail://auth-complete?code=abc123")));
        _profile.Setup(x => x.CompleteExternalSignInAsync("abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Success(account));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().BeSameAs(account);
        broker.AuthorizationUri.Should().Be(AuthorizeUri);
        broker.CallbackScheme.Should().Be(WinoAccountExternalAuth.CallbackScheme);
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheCallbackCarriesAnError_FailsWithoutCallingTheApi()
    {
        var broker = new FakeBroker(_ => Task.FromResult(new Uri("wino-mail://auth-complete?errorCode=EXTERNAL_LOGIN_INVALID")));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ApiErrorCodes.ExternalLoginInvalid);
        _profile.Verify(x => x.CompleteExternalSignInAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheCallbackHasNoCode_FailsWithCodeInvalid()
    {
        var broker = new FakeBroker(_ => Task.FromResult(new Uri("wino-mail://auth-complete")));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.ErrorCode.Should().Be(ApiErrorCodes.ExternalAuthCodeInvalid);
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheUserClosesTheBrowser_ReportsCancelled()
    {
        var broker = new FakeBroker(_ => throw new OperationCanceledException());
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.IsSuccess.Should().BeFalse();
        WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheRedirectNeverArrives_TimesOutAsCancelled()
    {
        var broker = new FakeBroker(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return WinoAccountExternalAuth.CallbackUri;
        });
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker) { BrowserTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await service.SignInWithAppleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode).Should().BeTrue();
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheBrowserCannotOpen_ReportsExternalSignInFailed()
    {
        var broker = new FakeBroker(_ => throw new InvalidOperationException("no window"));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ExternalSignInFailed);
        result.ErrorMessage.Should().Be("no window");
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheCallerCancels_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        var broker = new FakeBroker(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return WinoAccountExternalAuth.CallbackUri;
        });
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var pending = service.SignInWithAppleAsync(cancellation.Token);
        cancellation.Cancel();
        var act = () => pending.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Availability_FollowsTheRegisteredPieces()
    {
        new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object).Should().Match<WinoAccountExternalSignInService>(x => !x.IsAppleAvailable && !x.IsGoogleAvailable);
        new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, google: new FakeGoogle(_ => Task.FromResult<string?>("t"))).Should().Match<WinoAccountExternalSignInService>(x => !x.IsAppleAvailable && x.IsGoogleAvailable);
        new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: new FakeBroker(_ => Task.FromResult(WinoAccountExternalAuth.CallbackUri))).Should().Match<WinoAccountExternalSignInService>(x => x.IsAppleAvailable && x.IsGoogleAvailable);
    }

    [Fact]
    public async Task SignInWithAppleAsync_WithoutAnyPiece_Fails()
    {
        var result = await new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object).SignInWithAppleAsync();

        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ExternalSignInFailed);
    }

    [Fact]
    public async Task SignInWithAppleAsync_PrefersTheNativeProvider()
    {
        var account = new WinoAccount { Id = Guid.NewGuid(), Email = "apple@example.test" };
        var credential = new AppleSignInCredential("id", "code", "Ada", null);
        _profile.Setup(x => x.SignInWithAppleAsync(credential, It.IsAny<CancellationToken>())).ReturnsAsync(WinoAccountOperationResult.Success(account));
        var broker = new FakeBroker(_ => throw new InvalidOperationException("the browser must not open"));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, new FakeApple(_ => Task.FromResult<AppleSignInCredential?>(credential)), broker: broker);

        var result = await service.SignInWithAppleAsync();

        result.Account.Should().BeSameAs(account);
        broker.AuthorizationUri.Should().BeNull();
    }

    [Fact]
    public async Task SignInWithAppleAsync_WhenTheSheetIsDismissed_ReportsCancelled()
    {
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, new FakeApple(_ => Task.FromResult<AppleSignInCredential?>(null)));

        var result = await service.SignInWithAppleAsync();

        WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode).Should().BeTrue();
        _profile.Verify(x => x.SignInWithAppleAsync(It.IsAny<AppleSignInCredential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SignInWithGoogleAsync_SendsTheIdTokenToTheProfileService()
    {
        var account = new WinoAccount { Id = Guid.NewGuid(), Email = "google@example.test" };
        _profile.Setup(x => x.SignInWithGoogleAsync("id-token", It.IsAny<CancellationToken>())).ReturnsAsync(WinoAccountOperationResult.Success(account));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, google: new FakeGoogle(_ => Task.FromResult<string?>("id-token")));

        var result = await service.SignInWithGoogleAsync();

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().BeSameAs(account);
    }

    [Fact]
    public async Task SignInWithGoogleAsync_WhenTheProviderFailsOnTheDevice_ReportsItsCode()
    {
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object,
            google: new FakeGoogle(_ => throw new Wino.Core.Domain.Exceptions.WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, "bad token")));

        var result = await service.SignInWithGoogleAsync();

        result.ErrorCode.Should().Be(ApiErrorCodes.ExternalLoginInvalid);
        result.ErrorMessage.Should().Be("bad token");
    }

    [Fact]
    public async Task SignInWithGoogleAsync_WithoutANativeProvider_UsesTheBrowserFlow()
    {
        _apiClient.Setup(x => x.GetExternalSignInAuthorizationUri(WinoAccountExternalAuth.GoogleProvider)).Returns(new Uri("https://api.example.test/api/v1/auth/google/authorize"));
        _profile.Setup(x => x.CompleteExternalSignInAsync("g-code", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Success(new WinoAccount { Id = Guid.NewGuid(), Email = "google@example.test" }));
        var broker = new FakeBroker(_ => Task.FromResult(new Uri("wino-mail://auth-complete?code=g-code")));
        var service = new WinoAccountExternalSignInService(_apiClient.Object, _profile.Object, broker: broker);

        var result = await service.SignInWithGoogleAsync();

        result.IsSuccess.Should().BeTrue();
        broker.AuthorizationUri!.AbsolutePath.Should().Be("/api/v1/auth/google/authorize");
    }

    private sealed class FakeApple(Func<CancellationToken, Task<AppleSignInCredential?>> respond) : IAppleNativeSignInProvider
    {
        public Task<AppleSignInCredential?> RequestCredentialAsync(CancellationToken cancellationToken) => respond(cancellationToken);
    }

    private sealed class FakeGoogle(Func<CancellationToken, Task<string?>> respond) : IGoogleNativeSignInProvider
    {
        public Task<string?> RequestIdTokenAsync(CancellationToken cancellationToken) => respond(cancellationToken);
    }

    private sealed class FakeBroker(Func<CancellationToken, Task<Uri>> respond) : IWinoAccountBrowserAuthBroker
    {
        public Uri? AuthorizationUri { get; private set; }
        public string? CallbackScheme { get; private set; }

        public Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken)
        {
            AuthorizationUri = authorizationUri;
            CallbackScheme = callbackScheme;
            return respond(cancellationToken);
        }
    }
}
