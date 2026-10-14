using System.Net;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class WinoAccountApiClientRelayTokenTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await _database.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "user@example.test",
            AccessToken = "access",
            RefreshToken = "refresh",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            RefreshTokenExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        });
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task CreateRelayTokenAsync_PostsWithBearerAndParsesEnvelope()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.OK,
            "{\"isSuccess\":true,\"result\":{\"token\":\"relay-jwt\",\"expiresUtc\":\"2026-10-13T21:08:29+00:00\",\"relayUrl\":\"https://relay.winomail.app\"}}"));
        using var client = CreateClient(handler);

        var envelope = await client.CreateRelayTokenAsync();

        envelope.IsSuccess.Should().BeTrue();
        envelope.Result.Should().NotBeNull();
        envelope.Result!.Token.Should().Be("relay-jwt");
        envelope.Result.RelayUrl.Should().Be("https://relay.winomail.app");
        envelope.Result.ExpiresUtc.Should().Be(new DateTimeOffset(2026, 10, 13, 21, 8, 29, TimeSpan.Zero));
        envelope.Result.ToString().Should().NotContain("relay-jwt");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/relay/token");
        request.AuthScheme.Should().Be("Bearer");
        request.Bearer.Should().Be("access");
    }

    [Fact]
    public async Task CreateRelayTokenAsync_WhenRelayUnavailable_SurfacesErrorCode()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.ServiceUnavailable,
            "{\"isSuccess\":false,\"errorCode\":\"RELAY_UNAVAILABLE\"}"));
        using var client = CreateClient(handler);

        var envelope = await client.CreateRelayTokenAsync();

        envelope.IsSuccess.Should().BeFalse();
        envelope.ErrorCode.Should().Be(RelayApiErrorCodes.RelayUnavailable);
    }

    private WinoAccountApiClient CreateClient(RecordingHandler handler)
        => new(_database, TestAccountCredentialPersistence.Instance, new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") });
}
