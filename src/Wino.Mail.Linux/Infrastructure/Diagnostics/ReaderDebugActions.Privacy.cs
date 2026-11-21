#if DEBUG
using Wino.Editor;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-hover</c> <c>s</c> url (empty clears; <c>safe:</c> prefix = a Safe Links target),
/// <c>debug-reader-privacy</c> (opens the popover), <c>debug-reader-load-once</c>, <c>debug-reader-context-menu</c>
/// (GTK's keyboard context-menu action on the web view).
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterPrivacy(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-hover", "s", value =>
        {
            var bar = Current().DebugLinkTarget ?? throw new InvalidOperationException("The reader has no link bar.");
            if (value.Length == 0) bar.Clear();
            else if (value.StartsWith("safe:", StringComparison.Ordinal))
                bar.Show(new RendererLinkHoverEventArgs("https://nam01.safelinks.protection.outlook.com/?url=x", value["safe:".Length..]));
            else bar.Show(new RendererLinkHoverEventArgs(value, null));
        });
        Add(application, "debug-reader-privacy", null, _ => Current().DebugShowPrivacy());
        Add(application, "debug-reader-load-once", null, _ => Current().DebugLoadOnce());
        Add(application, "debug-reader-context-menu", null, _ =>
        {
            var view = Current().DebugWebView ?? throw new InvalidOperationException("The reader has no web view.");
            view.GrabFocus();
            Serilog.Log.Information("Debug action: menu.popup returned {Result}", view.ActivateAction("menu.popup", null));
        });
    }
}
#endif
