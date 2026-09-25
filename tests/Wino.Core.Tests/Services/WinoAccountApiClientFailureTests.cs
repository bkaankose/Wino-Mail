using System.Net;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountApiClientFailureTests : IAsyncLifetime
{
    private const string GatewayErrorPage = "<!DOCTYPE html><html><body><h1>502 Bad Gateway</h1></body></html>";

    private readonly InMemoryDatabaseService _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task LoginAsync_WhenServiceIsUnreachable_ReturnsServiceUnavailable()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("No connection could be made."));

        var result = await client.LoginAsync("user@example.test", "pw");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "text/html")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "text/plain")]
    [InlineData(HttpStatusCode.NotFound, "text/html")]
    public async Task LoginAsync_WhenServiceReturnsAnErrorPage_ReturnsServiceUnavailable(HttpStatusCode statusCode, string mediaType)
    {
        using var client = CreateClient(_ => Html(statusCode, mediaType));

        var result = await client.LoginAsync("user@example.test", "pw");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
        result.ErrorMessage.Should().BeNull("the HTML body must not leak into the error text");
    }

    [Fact]
    public async Task LoginAsync_WhenSuccessResponseIsNotApiJson_ReturnsInvalidServiceResponse()
    {
        using var client = CreateClient(_ => Html(HttpStatusCode.OK, "text/plain"));

        var result = await client.LoginAsync("user@example.test", "pw");

        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.InvalidServiceResponse);
    }

    [Fact]
    public async Task ForgotPasswordAsync_WhenRequestTimesOut_ReturnsServiceUnavailable()
    {
        using var client = CreateClient(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

        var result = await client.ForgotPasswordAsync("user@example.test");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task ForgotPasswordAsync_WhenCallerCancels_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = CreateClient(_ => throw new TaskCanceledException());

        var act = () => client.ForgotPasswordAsync("user@example.test", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSettingsAsync_WhenServiceIsUnreachable_ThrowsServiceUnavailable()
    {
        await InsertAccountAsync(DateTime.UtcNow.AddHours(1));
        using var client = CreateClient(_ => throw new HttpRequestException("No connection could be made."));

        var act = () => client.GetSettingsAsync();

        (await act.Should().ThrowAsync<WinoAccountApiException>())
            .Which.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task GetMailboxesAsync_WhenServiceReturnsAnErrorPage_ThrowsServiceUnavailable()
    {
        await InsertAccountAsync(DateTime.UtcNow.AddHours(1));
        using var client = CreateClient(_ => Html(HttpStatusCode.BadGateway, "text/html"));

        var act = () => client.GetMailboxesAsync();

        (await act.Should().ThrowAsync<WinoAccountApiException>())
            .Which.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task GetSettingsAsync_WithoutAccount_ThrowsSignInRequired()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("No request expected."));

        var act = () => client.GetSettingsAsync();

        (await act.Should().ThrowAsync<WinoAccountApiException>())
            .Which.ErrorCode.Should().Be(WinoAccountClientErrorCodes.SignInRequired);
    }

    [Fact]
    public async Task GetCurrentUserAsync_WhenRefreshAfterUnauthorizedCannotReachService_ReturnsServiceUnavailable()
    {
        await InsertAccountAsync(DateTime.UtcNow.AddHours(1));
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath == "/api/v1/auth/refresh"
            ? throw new HttpRequestException("No connection could be made.")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.GetCurrentUserAsync();

        result.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
        (await _database.Connection.Table<WinoAccount>().FirstAsync()).RefreshToken.Should().Be("refresh");
    }

    [Fact]
    public async Task ProfileService_WhenExpiredTokenCannotBeRefreshed_ReportsServiceUnavailableInsteadOfMissingToken()
    {
        await InsertAccountAsync(DateTime.UtcNow.AddHours(-1));
        using var client = CreateClient(_ => throw new HttpRequestException("No connection could be made."));
        var profileService = new WinoAccountProfileService(_database, client);

        var getSettings = () => profileService.GetSettingsAsync();
        var getAccount = () => profileService.GetAuthenticatedAccountAsync();
        var profile = await profileService.GetCurrentUserAsync();

        (await getSettings.Should().ThrowAsync<WinoAccountApiException>())
            .Which.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
        (await getAccount.Should().ThrowAsync<WinoAccountApiException>())
            .Which.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
        profile.ErrorCode.Should().Be(WinoAccountClientErrorCodes.ServiceUnavailable);
        (await profileService.GetActiveAccountAsync()).Should().NotBeNull("an outage must not sign the user out");
    }

    private WinoAccountApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(_database, new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.example.test/") });

    private Task InsertAccountAsync(DateTime accessTokenExpiresAtUtc)
        => _database.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "user@example.test",
            AccessToken = "access",
            RefreshToken = "refresh",
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            RefreshTokenExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        });

    private static HttpResponseMessage Html(HttpStatusCode statusCode, string mediaType)
        => new(statusCode) { Content = new StringContent(GatewayErrorPage, Encoding.UTF8, mediaType) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
