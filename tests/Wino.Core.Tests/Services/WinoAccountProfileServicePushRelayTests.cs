using System.Text.Json;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountProfileServicePushRelayTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private readonly Mock<IWinoAccountApiClient> _api = new();
    private readonly Mock<IPushRelayRegistrationService> _relay = new();

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
        _api.Setup(x => x.LogoutAsync("refresh", It.IsAny<CancellationToken>())).ReturnsAsync(ApiEnvelope<JsonElement>.Success(default));
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task SignOutAsync_SignsOutOfTheRelayWhileTheSessionStillExists()
    {
        var sessions = WinoAccountSessionService.For(_database, TestAccountCredentialPersistence.Instance);
        WinoAccountSession? sessionDuringRelaySignOut = null;
        _relay.Setup(x => x.SignOutAsync(It.IsAny<CancellationToken>()))
            .Returns(async () => { sessionDuringRelaySignOut = await sessions.CaptureAsync(); });
        var service = CreateService(sessions);

        await service.SignOutAsync();

        _relay.Verify(x => x.SignOutAsync(It.IsAny<CancellationToken>()), Times.Once);
        sessionDuringRelaySignOut.Should().NotBeNull("the relay token needs the Wino tokens");
        (await service.GetActiveAccountAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SignOutAsync_WhenTheRelayFails_StillSignsOut()
    {
        _relay.Setup(x => x.SignOutAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("relay down"));
        var service = CreateService(WinoAccountSessionService.For(_database, TestAccountCredentialPersistence.Instance));

        await service.SignOutAsync();

        (await service.GetActiveAccountAsync()).Should().BeNull();
    }

    private WinoAccountProfileService CreateService(IWinoAccountSessionService sessions)
        => new(_database, _api.Object, TestAccountCredentialPersistence.Instance, sessionService: sessions, pushRelay: () => _relay.Object);
}
