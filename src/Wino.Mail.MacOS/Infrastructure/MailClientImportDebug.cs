#if DEBUG
namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Debug bridge: <c>import-folder PATH</c> makes the import sheet scan PATH in place of the open panel, and
/// <c>import-folder</c> with no path restores the panel. The import itself runs the real command and sheet.
/// </summary>
internal static class MailClientImportDebug
{
    public static void Register()
    {
        MacDebugBridge.Register("import-folder", args =>
        {
            AppKitDialogService.DebugThunderbirdFolder = args.Length > 0 ? string.Join(' ', args) : null;
            return Task.FromResult(AppKitDialogService.DebugThunderbirdFolder ?? "panel");
        });
    }
}
#endif
