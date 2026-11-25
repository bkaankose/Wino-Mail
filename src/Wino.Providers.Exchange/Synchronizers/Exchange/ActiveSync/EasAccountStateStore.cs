using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Services;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed class EasAccountStateStore(IDatabaseService database)
{
    internal async Task<EasAccountState> LoadAsync(Guid accountId)
    {
        var state = await database.Connection.FindAsync<EasAccountState>(accountId).ConfigureAwait(false);
        if (state != null)
            return state;

        // The account id was the device id before the state was stored, so existing partnerships stay valid.
        state = new EasAccountState { AccountId = accountId, DeviceId = accountId.ToString("N") };
        await database.Connection.InsertOrReplaceAsync(state, typeof(EasAccountState)).ConfigureAwait(false);
        return state;
    }

    internal Task SaveAsync(EasAccountState state)
        => database.Connection.InsertOrReplaceAsync(state, typeof(EasAccountState));
}
