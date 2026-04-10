using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Activation;

namespace Wino.Mail.WinUI.Services;

internal sealed class PackagedAppEntryLauncher
{
    public async Task<bool> LaunchAsync(WinoApplicationMode mode)
    {
        var targetApplicationId = AppEntryConstants.GetPackagedApplicationId(mode);
        if (string.IsNullOrWhiteSpace(targetApplicationId))
            return false;

        var targetAppUserModelId = AppEntryConstants.GetAppUserModelId(mode);
        var appEntries = await Package.Current.GetAppListEntriesAsync();
        var appEntry = appEntries.FirstOrDefault(entry =>
            string.Equals(entry.AppUserModelId, targetAppUserModelId, StringComparison.OrdinalIgnoreCase));

        return appEntry != null && await appEntry.LaunchAsync();
    }
}
