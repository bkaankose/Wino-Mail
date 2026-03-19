#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services;

public sealed class WinoAddOnService : IWinoAddOnService
{
    private readonly IWinoAccountProfileService _profileService;

    public WinoAddOnService(IWinoAccountProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task<IReadOnlyList<WinoAddOnInfo>> GetAvailableAddOnsAsync(bool useCachedDataOnly = false, CancellationToken cancellationToken = default)
    {
        var cachedSnapshot = await _profileService.GetCachedAddOnSnapshotAsync().ConfigureAwait(false);

        if (useCachedDataOnly)
        {
            return BuildAddOnInfos(cachedSnapshot);
        }

        var aiStatusTask = _profileService.GetAiStatusAsync(cancellationToken);
        var hasUnlimitedAccountsTask = _profileService.HasAddOnAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, cancellationToken);

        await Task.WhenAll(aiStatusTask, hasUnlimitedAccountsTask).ConfigureAwait(false);

        var aiStatusResponse = await aiStatusTask.ConfigureAwait(false);
        var aiStatus = aiStatusResponse.IsSuccess ? aiStatusResponse.Result : null;
        var hasUnlimitedAccounts = await hasUnlimitedAccountsTask.ConfigureAwait(false);

        if (aiStatus == null && cachedSnapshot != null)
        {
            return BuildAddOnInfos(cachedSnapshot with
            {
                HasUnlimitedAccounts = hasUnlimitedAccounts || cachedSnapshot.HasUnlimitedAccounts
            });
        }

        return
        [
            new WinoAddOnInfo(
                WinoAddOnProductType.AI_PACK,
                aiStatus?.HasAiPack == true,
                aiStatus?.Used,
                aiStatus?.MonthlyLimit,
                aiStatus?.HasAiPack == true && aiStatus.MonthlyLimit is int limit && limit > 0 && aiStatus.Used is int used
                    ? (double)used / limit * 100
                    : 0,
                aiStatus?.CurrentPeriodEndUtc),
            new WinoAddOnInfo(
                WinoAddOnProductType.UNLIMITED_ACCOUNTS,
                hasUnlimitedAccounts || cachedSnapshot?.HasUnlimitedAccounts == true)
        ];
    }

    private static IReadOnlyList<WinoAddOnInfo> BuildAddOnInfos(WinoAccountAddOnSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return [];
        }

        return
        [
            new WinoAddOnInfo(
                WinoAddOnProductType.AI_PACK,
                snapshot.HasAiPack,
                snapshot.UsageCount,
                snapshot.UsageLimit,
                snapshot.HasAiPack && snapshot.UsageLimit is int limit && limit > 0 && snapshot.UsageCount is int used
                    ? (double)used / limit * 100
                    : 0,
                snapshot.BillingPeriodEndUtc),
            new WinoAddOnInfo(
                WinoAddOnProductType.UNLIMITED_ACCOUNTS,
                snapshot.HasUnlimitedAccounts)
        ];
    }
}
