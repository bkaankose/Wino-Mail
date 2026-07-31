using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IMimeStorageService
{
    Task<string> GetMimeRootPathAsync();
    Task<Dictionary<Guid, long>> GetAccountsMimeStorageSizesAsync(IEnumerable<Guid> accountIds);
    Task DeleteAccountMimeStorageAsync(Guid accountId);
    Task<int> DeleteAccountMimeStorageOlderThanAsync(Guid accountId, DateTime cutoffDateUtc);
}
