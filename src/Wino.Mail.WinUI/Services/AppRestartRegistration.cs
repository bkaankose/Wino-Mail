using System.Runtime.InteropServices;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Asks Windows to relaunch Wino after a package update closes it. Packaged desktop apps are not
/// relaunched unless they register before the installer shuts them down.
/// </summary>
internal static partial class AppRestartRegistration
{
    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoReboot = 8;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string? commandLine, int flags);

    [LibraryImport("kernel32.dll")]
    private static partial int UnregisterApplicationRestart();

    public static bool RegisterForUpdate()
    {
        var result = RegisterApplicationRestart(null, RestartNoCrash | RestartNoHang | RestartNoReboot);

        if (result != 0)
            Serilog.Log.Warning("Restart registration for the update failed (HRESULT 0x{HResult:X8}).", result);

        return result == 0;
    }

    public static void Unregister() => UnregisterApplicationRestart();
}
