#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class AccountOAuthProfileService : BaseDatabaseService, IAccountOAuthProfileService
{
    public AccountOAuthProfileService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public async Task<AccountOAuthProfile?> GetAsync(Guid accountId)
    {
        if (accountId == Guid.Empty)
            return null;

        return await Connection.FindAsync<AccountOAuthProfile>(accountId).ConfigureAwait(false);
    }

    public Task SaveAsync(AccountOAuthProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.AccountId == Guid.Empty)
            throw new ArgumentException("An OAuth profile must belong to an account.", nameof(profile));

        return Connection.InsertOrReplaceAsync(profile, typeof(AccountOAuthProfile));
    }

    public Task DeleteAsync(Guid accountId)
        => Connection.DeleteAsync<AccountOAuthProfile>(accountId);
}
