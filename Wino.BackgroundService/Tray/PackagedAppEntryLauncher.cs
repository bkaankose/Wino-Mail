using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;

namespace Wino.BackgroundService.Tray;

/// <summary>
/// Launches the packaged UI app entries (Mail / Calendar) from the tray menu and
/// toast forwarding paths.
/// </summary>
public sealed class PackagedAppEntryLauncher
{
    public Task<bool> LaunchMailAsync() => LaunchAsync(AppEntryConstants.MailApplicationId);

    public Task<bool> LaunchCalendarAsync() => LaunchAsync(AppEntryConstants.CalendarApplicationId);

    public async Task<bool> LaunchAsync(string applicationId)
    {
        try
        {
            var targetAppUserModelId = AppEntryConstants.GetAppUserModelId(applicationId);
            var appEntries = await Package.Current.GetAppListEntriesAsync();
            var appEntry = appEntries.FirstOrDefault(entry =>
                string.Equals(entry.AppUserModelId, targetAppUserModelId, StringComparison.OrdinalIgnoreCase));

            return appEntry != null && await appEntry.LaunchAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to launch app entry {ApplicationId}.", applicationId);
            return false;
        }
    }

    /// <summary>
    /// Activates the Mail UI entry with explicit arguments (toast forwarding).
    /// </summary>
    public bool TryActivateMailWithArguments(string arguments)
    {
        try
        {
            var aumid = AppEntryConstants.GetAppUserModelId(AppEntryConstants.MailApplicationId);
            var activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
            activationManager.ActivateApplication(aumid, arguments, ActivateOptions.None, out _);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to activate Mail UI with arguments.");
            return false;
        }
    }

    private enum ActivateOptions
    {
        None = 0x00000000,
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication(
            [System.Runtime.InteropServices.In] string appUserModelId,
            [System.Runtime.InteropServices.In] string arguments,
            [System.Runtime.InteropServices.In] ActivateOptions options,
            [System.Runtime.InteropServices.Out] out uint processId);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
