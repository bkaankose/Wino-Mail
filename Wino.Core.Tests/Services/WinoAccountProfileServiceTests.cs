using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Wino.Core.Tests.Helpers;
using Xunit;

namespace Wino.Core.Tests.Services;

public class WinoAccountProfileServiceTests : IAsyncLifetime
{
    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private InMemoryDatabaseService _databaseService = null!;
    private WinoAccountProfileService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new WinoAccountProfileService(_databaseService, _apiClient.Object);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task LoginAsync_ShouldPersistSingleActiveAccount()
    {
        var authResult = CreateAuthResult("first@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(ApiEnvelope<AuthResultDto>.Success(authResult));

        var result = await _service.LoginAsync("first@example.com", "pw");

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().NotBeNull();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().ContainSingle();
        persisted[0].Email.Should().Be("first@example.com");
        persisted[0].AccessToken.Should().Be(authResult.AccessToken);
        persisted[0].RefreshToken.Should().Be(authResult.RefreshToken);
    }

    [Fact]
    public async Task LoginAsync_ShouldReplaceExistingActiveAccount()
    {
        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(ApiEnvelope<AuthResultDto>.Success(CreateAuthResult("first@example.com")));

        _apiClient
            .Setup(x => x.LoginAsync("second@example.com", "pw", default))
            .ReturnsAsync(ApiEnvelope<AuthResultDto>.Success(CreateAuthResult("second@example.com")));

        await _service.LoginAsync("first@example.com", "pw");
        await _service.LoginAsync("second@example.com", "pw");

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().ContainSingle();
        persisted[0].Email.Should().Be("second@example.com");
    }

    [Fact]
    public async Task SignOutAsync_ShouldDeletePersistedAccount()
    {
        var authResult = CreateAuthResult("signout@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("signout@example.com", "pw", default))
            .ReturnsAsync(ApiEnvelope<AuthResultDto>.Success(authResult));

        _apiClient
            .Setup(x => x.LogoutAsync(authResult.RefreshToken, default))
            .ReturnsAsync(ApiEnvelope<System.Text.Json.JsonElement>.Success(default));

        await _service.LoginAsync("signout@example.com", "pw");
        await _service.SignOutAsync();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().BeEmpty();
    }

    private static AuthResultDto CreateAuthResult(string email)
    {
        return new AuthResultDto(
            new AuthUserDto(Guid.NewGuid(), email, "Active", true, false, false),
            "access-token",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(30));
    }
}
