using System.Web;
using Moq;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Platform;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GmailAuthenticatorStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-state")]
    public async Task GenerateTokenInformationAsync_RejectsMissingOrMismatchedState(string? returnedState)
    {
        using var httpClient = new HttpClient();
        var nativeAppService = new Mock<IExternalLauncher>();
        Uri? authorizationUri = null;
        Task<string>? browserResponse = null;

        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Returns((Uri uri, CancellationToken cancellationToken) =>
            {
                authorizationUri = uri;
                var redirectUri = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"]!;
                var stateParameter = returnedState is null ? "" : $"&state={returnedState}";
                browserResponse = httpClient.GetStringAsync($"{redirectUri}?code=unused{stateParameter}");
                return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
            });

        var configuration = new MailAuthenticatorConfiguration(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = Path.GetTempPath()
        });
        var authenticator = new GmailAuthenticator(configuration, nativeAppService.Object, authenticationPresenter: null, tokenStore: new TestGoogleTokenStore());
        var account = new MailAccount { Id = Guid.NewGuid() };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GenerateTokenInformationAsync(account));

        Assert.Contains("invalid state", exception.Message);
        Assert.False(string.IsNullOrWhiteSpace(HttpUtility.ParseQueryString(authorizationUri!.Query)["state"]));
        Assert.Contains("Authorization failed", await browserResponse!);
    }

    [Fact]
    public async Task GenerateTokenInformationAsync_AcceptsMatchingStateBeforeHandlingGoogleError()
    {
        using var httpClient = new HttpClient();
        var nativeAppService = new Mock<IExternalLauncher>();
        Task<string>? browserResponse = null;

        nativeAppService.Setup(service => service.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Returns((Uri uri, CancellationToken cancellationToken) =>
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                var redirectUri = query["redirect_uri"]!;
                var state = Uri.EscapeDataString(query["state"]!);
                browserResponse = httpClient.GetStringAsync($"{redirectUri}?error=access_denied&state={state}");
                return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
            });

        var configuration = new MailAuthenticatorConfiguration(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = Path.GetTempPath()
        });
        var authenticator = new GmailAuthenticator(configuration, nativeAppService.Object, authenticationPresenter: null, tokenStore: new TestGoogleTokenStore());
        var account = new MailAccount { Id = Guid.NewGuid() };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GenerateTokenInformationAsync(account));

        Assert.Contains("Google authorization failed: access_denied", exception.Message);
        Assert.Contains("Authorization failed", await browserResponse!);
    }
}
