#if DEBUG
using System.Text;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the view foundation. <c>debug-live-objects</c> <c>s</c>: writes one line <c>Type count</c> per
/// tracked view type that is still alive after a full collection, for leak checks.
/// </summary>
internal static class FoundationDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-live-objects", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                if (args.Parameter?.GetString(out var _) is not { Length: > 0 } path) throw new ArgumentException("An output path is required.");
                var text = new StringBuilder();
                foreach (var (type, count) in WinoLiveObjects.Snapshot()) text.Append(type).Append(' ').Append(count).AppendLine();
                var temporary = path + ".partial";
                File.WriteAllText(temporary, text.ToString());
                File.Move(temporary, path, overwrite: true);
                Serilog.Log.Information("Debug action: live objects written to {Path}.", path);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-live-objects failed."); }
        };
        application.AddAction(action);
    }
}
#endif
