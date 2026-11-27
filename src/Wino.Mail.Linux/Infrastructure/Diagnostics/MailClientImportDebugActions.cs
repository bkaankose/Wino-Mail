#if DEBUG
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// The import from other mail clients dialog: <c>debug-mail-client-import</c> (<c>as</c>) <c>['state', path]</c> writes
/// the open dialog's rows, <c>['toggle', title]</c> clicks a row's check box, which AT-SPI cannot do.
/// </summary>
internal static class MailClientImportDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-mail-client-import", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            var verb = parts.Length > 0 ? parts[0] : string.Empty;
            var value = parts.Length > 1 ? parts[1] : string.Empty;
            switch (verb)
            {
                case "state":
                    DebugActions.WriteText(value, MailClientImportDialog.DebugDescribe());
                    Serilog.Log.Information("Debug action: mail client import state written to {Path}.", value);
                    break;
                case "toggle":
                    Serilog.Log.Information("Debug action: mail client import toggle {Result}.", MailClientImportDialog.DebugToggle(value));
                    break;
                default:
                    Serilog.Log.Warning("Debug action: unknown mail client import verb {Verb}.", verb);
                    break;
            }
        };
        application.AddAction(action);
    }
}
#endif
