using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountApiClientExternalSignInTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private readonly List<(string Path, JsonElement Body)> _requests = [];

    public Task InitializeAsync() => _database.InitializeAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task SignInWithAppleNativeAsync_PostsTheCredentialToTheNativeRoute()
    {
        using var client = CreateClient(_ => Success(hasAppleLogin: true));

        var result = await client.SignInWithAppleNativeAsync("id-token", "auth-code", "Ada", "Lovelace");

        result.IsSuccess.Should().BeTrue();
        result.Result!.User.HasAppleLogin.Should().BeTrue();
        var (path, body) = _requests.Should().ContainSingle().Subject;
        path.Should().Be("/api/v1/auth/apple/native");
        Property(body, "identityToken").GetString().Should().Be("id-token");
        Property(body, "authorizationCode").GetString().Should().Be("auth-code");
        Property(body, "givenName").GetString().Should().Be("Ada");
        Property(body, "familyName").GetString().Should().Be("Lovelace");
    }

    [Fact]
    public async Task SignInWithAppleNativeAsync_WithoutAName_SendsNulls()
    {
        using var client = CreateClient(_ => Success());

        await client.SignInWithAppleNativeAsync("id-token", "auth-code", null, null);

        var body = _requests.Should().ContainSingle().Subject.Body;
        Property(body, "givenName").ValueKind.Should().Be(JsonValueKind.Null);
        Property(body, "familyName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CompleteExternalSignInAsync_PostsTheCodeToTheCompleteRoute()
    {
        using var client = CreateClient(_ => Success());

        var result = await client.CompleteExternalSignInAsync("completion-code");

        result.IsSuccess.Should().BeTrue();
        var (path, body) = _requests.Should().ContainSingle().Subject;
        path.Should().Be("/api/v1/auth/external/complete");
        Property(body, "code").GetString().Should().Be("completion-code");
    }

    [Fact]
    public async Task SignInWithAppleNativeAsync_ReturnsTheApiErrorCode()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { isSuccess = false, errorCode = ApiErrorCodes.ExternalLoginInvalid, message = "Token rejected." }), Encoding.UTF8, "application/json")
        });

        var result = await client.SignInWithAppleNativeAsync("id-token", "auth-code", null, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ApiErrorCodes.ExternalLoginInvalid);
        result.ErrorMessage.Should().Be("Token rejected.");
    }

    [Fact]
    public void GetExternalSignInAuthorizationUri_IsTheProviderRouteOnTheApiHost()
    {
        using var client = CreateClient(_ => Success());

        client.GetExternalSignInAuthorizationUri(WinoAccountExternalAuth.AppleProvider)
            .Should().Be(new Uri("https://api.example.test/api/v1/auth/apple/authorize"));
    }

    /// <summary>The API binds request bodies case-insensitively, and the client's JSON context has no naming policy.</summary>
    private static JsonElement Property(JsonElement body, string name)
    {
        foreach (var property in body.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new Xunit.Sdk.XunitException($"The request body has no '{name}' property: {body}");
    }

    private WinoAccountApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(_database, TestAccountCredentialPersistence.Instance, new HttpClient(new RecordingHandler(this, respond)) { BaseAddress = new Uri("https://api.example.test/") });

    private static HttpResponseMessage Success(bool hasAppleLogin = false)
    {
        var user = new
        {
            userId = Guid.NewGuid(), email = "user@example.test", accountStatus = "Active", hasPassword = false,
            hasGoogleLogin = false, hasFacebookLogin = false, isUnlimitedAccountsEnabled = false, hasAppleLogin
        };
        var result = new
        {
            user, accessToken = "access", refreshToken = "refresh",
            accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { isSuccess = true, result }), Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler(WinoAccountApiClientExternalSignInTests owner, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "null" : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.RequestUri!.AbsolutePath, JsonDocument.Parse(body).RootElement.Clone()));
            return respond(request);
        }
    }
}
