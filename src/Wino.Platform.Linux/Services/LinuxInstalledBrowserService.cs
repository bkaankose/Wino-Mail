using System.Runtime.InteropServices;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Browsers;

namespace Wino.Platform.Linux.Services;

/// <summary>
/// Browsers are the handlers of <c>x-scheme-handler/https</c> in the freedesktop MIME registry (desktop files), read
/// through GIO. Linux browsers have no profiles here. Every call runs on the GTK main thread and never throws.
/// </summary>
public sealed partial class LinuxInstalledBrowserService(IDispatcher dispatcher) : IInstalledBrowserService
{
    private const string HttpsHandler = "x-scheme-handler/https";
    private const string OwnDesktopId = "app.winomail.Mail.desktop";
    private const string GLibLibrary = "libglib-2.0.so.0";
    private const string GioLibrary = "libgio-2.0.so.0";

    public bool IsSupported => true;

    public async Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default)
    {
        var catalog = BrowserCatalog.Unavailable;
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var browsers = new List<BrowserOption>();
                foreach (var info in Handlers())
                {
                    var id = info.GetId();
                    if (string.IsNullOrEmpty(id) || string.Equals(id, OwnDesktopId, StringComparison.Ordinal)) continue;
                    var name = info.GetDisplayName();
                    browsers.Add(new BrowserOption(id, null, string.IsNullOrWhiteSpace(name) ? id : name));
                }
                browsers.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName));
                catalog = new BrowserCatalog(true, browsers);
            });
        }
        catch (Exception)
        {
            return BrowserCatalog.Unavailable;
        }
        return catalog;
    }

    public async Task<bool> TryLaunchAsync(BrowserOption browser, Uri uri, CancellationToken cancellationToken = default)
    {
        var launched = false;
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var info = Handlers().FirstOrDefault(candidate => string.Equals(candidate.GetId(), browser.BrowserId, StringComparison.OrdinalIgnoreCase));
                if (info is null) return;
                launched = Launch(info, uri.AbsoluteUri);
            });
        }
        catch (Exception)
        {
            return false;
        }
        return launched;
    }

    private static List<Gio.AppInfo> Handlers()
    {
        var handlers = new List<Gio.AppInfo>();
        var list = Gio.AppInfoHelper.GetAllForType(HttpsHandler);
        var count = GLib.List.Length(list);
        for (uint index = 0; index < count; index++)
        {
            // The entries are GDesktopAppInfo objects, a type this binding does not wrap; the generic wrapper takes a reference.
            if (GObject.Internal.InstanceWrapper.WrapHandle<GObject.Object>(GLib.List.NthData(list, index), false) is Gio.AppInfo info) handlers.Add(info);
        }
        return handlers;
    }

    // Gio.AppInfo.LaunchUris takes a GList of strings, which this binding cannot build; the list is made natively.
    private static bool Launch(Gio.AppInfo info, string uri)
    {
        if (info is not GObject.Object instance) return false;
        var text = Marshal.StringToCoTaskMemUTF8(uri);
        var list = IntPtr.Zero;
        try
        {
            list = g_list_append(IntPtr.Zero, text);
            return g_app_info_launch_uris(instance.Handle.DangerousGetHandle(), list, IntPtr.Zero, IntPtr.Zero) != 0;
        }
        finally
        {
            if (list != IntPtr.Zero) g_list_free(list);
            Marshal.FreeCoTaskMem(text);
        }
    }

    [LibraryImport(GLibLibrary)]
    private static partial IntPtr g_list_append(IntPtr list, IntPtr data);

    [LibraryImport(GLibLibrary)]
    private static partial void g_list_free(IntPtr list);

    [LibraryImport(GioLibrary)]
    private static partial int g_app_info_launch_uris(IntPtr appInfo, IntPtr uris, IntPtr context, IntPtr error);
}
