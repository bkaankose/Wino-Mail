using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class AccountProviderFeatureService(IDatabaseService databaseService)
    : BaseDatabaseService(databaseService), IAccountProviderFeatureService
{
    public async Task<IReadOnlyList<AccountProviderFeature>> GetFeaturesAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Connection.Table<AccountProviderFeature>()
            .Where(item => item.MailAccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public Task<AccountProviderFeature> GetFeatureAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Connection.Table<AccountProviderFeature>()
            .FirstOrDefaultAsync(item => item.MailAccountId == accountId && item.Feature == feature);
    }

    public async Task<bool> IsEnabledAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
        => (await GetFeatureAsync(accountId, feature, cancellationToken).ConfigureAwait(false))
            ?.AuthorizationState == ProviderFeatureAuthorizationState.Active;

    public async Task UpsertAsync(AccountProviderFeature feature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feature);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await GetFeatureAsync(feature.MailAccountId, feature.Feature, cancellationToken)
            .ConfigureAwait(false);
        if (existing == null)
        {
            feature.Id = feature.Id == Guid.Empty ? Guid.NewGuid() : feature.Id;
            await Connection.InsertAsync(feature, typeof(AccountProviderFeature)).ConfigureAwait(false);
            return;
        }

        feature.Id = existing.Id;
        feature.EnabledAtUtc = existing.EnabledAtUtc;
        await Connection.UpdateAsync(feature, typeof(AccountProviderFeature)).ConfigureAwait(false);
    }

    public Task DeleteAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Connection.Table<AccountProviderFeature>()
            .DeleteAsync(item => item.MailAccountId == accountId && item.Feature == feature);
    }

    public Task DeleteForAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Connection.Table<AccountProviderFeature>()
            .DeleteAsync(item => item.MailAccountId == accountId);
    }
}
