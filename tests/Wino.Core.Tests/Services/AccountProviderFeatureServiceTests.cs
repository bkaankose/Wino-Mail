using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountProviderFeatureServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private AccountProviderFeatureService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        await _databaseService.Connection.CreateTableAsync<AccountProviderFeature>();
        await _databaseService.Connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_AccountProviderFeature_Account_Feature ON AccountProviderFeature(MailAccountId, Feature)");
        _service = new AccountProviderFeatureService(_databaseService);
    }

    public Task DisposeAsync() => _databaseService.DisposeAsync().AsTask();

    [Fact]
    public async Task MissingRecord_IsNotEnabled()
    {
        var enabled = await _service.IsEnabledAsync(Guid.NewGuid(), ProviderFeature.MailFilters);

        Assert.False(enabled);
    }

    [Fact]
    public async Task ActiveRecord_IsEnabled_AndReauthorizationRecordIsNot()
    {
        var accountId = Guid.NewGuid();
        var feature = new AccountProviderFeature
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Feature = ProviderFeature.MailFilters,
            AuthorizationState = ProviderFeatureAuthorizationState.Active,
            EnabledAtUtc = DateTime.UtcNow,
            LastAuthorizedAtUtc = DateTime.UtcNow
        };

        await _service.UpsertAsync(feature);
        Assert.True(await _service.IsEnabledAsync(accountId, ProviderFeature.MailFilters));

        feature.AuthorizationState = ProviderFeatureAuthorizationState.ReauthorizationRequired;
        await _service.UpsertAsync(feature);
        Assert.False(await _service.IsEnabledAsync(accountId, ProviderFeature.MailFilters));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOptInRecord()
    {
        var accountId = Guid.NewGuid();
        await _service.UpsertAsync(new AccountProviderFeature
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Feature = ProviderFeature.MailFilters,
            AuthorizationState = ProviderFeatureAuthorizationState.Active,
            EnabledAtUtc = DateTime.UtcNow
        });

        await _service.DeleteAsync(accountId, ProviderFeature.MailFilters);

        Assert.Null(await _service.GetFeatureAsync(accountId, ProviderFeature.MailFilters));
    }
}
