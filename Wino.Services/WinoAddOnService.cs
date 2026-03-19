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
    private static readonly WinoAddOnProductType[] AvailableAddOns =
    [
        WinoAddOnProductType.AI_PACK,
        WinoAddOnProductType.UNLIMITED_ACCOUNTS
    ];

    private readonly IWinoAccountProfileService _profileService;

    public WinoAddOnService(IWinoAccountProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task<IReadOnlyList<WinoAddOnInfo>> GetAvailableAddOnsAsync(CancellationToken cancellationToken = default)
    {
        var aiStatusTask = _profileService.GetAiStatusAsync(cancellationToken);
        var hasUnlimitedAccountsTask = _profileService.HasAddOnAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, cancellationToken);

        await Task.WhenAll(aiStatusTask, hasUnlimitedAccountsTask).ConfigureAwait(false);

        var aiStatusResponse = await aiStatusTask.ConfigureAwait(false);
        var aiStatus = aiStatusResponse.IsSuccess ? aiStatusResponse.Result : null;

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
                await hasUnlimitedAccountsTask.ConfigureAwait(false))
        ];
    }
}
