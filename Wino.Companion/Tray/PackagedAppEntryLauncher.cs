using Windows.ApplicationModel;
using Windows.System;

namespace Wino.Companion.Tray;

public sealed class PackagedAppEntryLauncher
{
    public Task<bool> LaunchMailAsync() => LaunchAsync("App");
    public Task<bool> LaunchCalendarAsync() => Launcher.LaunchUriAsync(new Uri("wino://calendar")).AsTask();

    private static async Task<bool> LaunchAsync(string applicationId)
    {
        var appUserModelId = $"{Package.Current.Id.FamilyName}!{applicationId}";
        var entries = await Package.Current.GetAppListEntriesAsync();
        var entry = entries.FirstOrDefault(value =>
            string.Equals(value.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase));
        return entry is not null && await entry.LaunchAsync();
    }
}
