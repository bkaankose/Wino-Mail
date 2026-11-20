using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.UI.StartScreen;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Activation;

namespace Wino.Mail.WinUI.Services.WidgetsAndTiles;

/// <summary>
/// Pins the Wino Mail tile to Start. Windows 11's own Start menu also reports pinning as supported, so the answer
/// says whether a pin is possible, not whether the Start menu shows Live Tiles.
/// </summary>
internal static class MailTileStartPin
{
    /// <summary>True when Start accepts the Wino Mail entry and it is not pinned yet.</summary>
    public static async Task<bool> CanRequestPinAsync()
    {
        try
        {
            var entry = await GetMailEntryAsync();
            if (entry == null)
                return false;

            var startScreen = StartScreenManager.GetDefault();
            return startScreen.SupportsAppListEntry(entry) && !await startScreen.ContainsAppListEntryAsync(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Live Tiles: could not read whether Wino Mail is pinned to Start.");
            return false;
        }
    }

    /// <summary>Shows the system's pin confirmation. Returns true when the tile is pinned afterwards.</summary>
    public static async Task<bool> RequestPinAsync()
    {
        try
        {
            var entry = await GetMailEntryAsync();
            return entry != null && await StartScreenManager.GetDefault().RequestAddAppListEntryAsync(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Live Tiles: pinning Wino Mail to Start failed.");
            return false;
        }
    }

    private static async Task<AppListEntry?> GetMailEntryAsync()
    {
        var mailAppUserModelId = AppEntryConstants.GetAppUserModelId(WinoApplicationMode.Mail);
        var entries = await Package.Current.GetAppListEntriesAsync();
        return entries.FirstOrDefault(entry =>
            string.Equals(entry.AppUserModelId, mailAppUserModelId, StringComparison.OrdinalIgnoreCase));
    }
}
