#if DEBUG
namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-command</c> <c>s</c> id (Reply, SetFlag, EditorTheme, ReaderView, Move, …; shown or in More),
/// <c>debug-reader-more</c> (opens the More menu), <c>debug-reader-labels</c> <c>s</c> on|off (this bar only).
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterCommands(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-command", "s", value =>
        {
            if (!Current().DebugRunCommand(value)) Serilog.Log.Information("Debug action: reader command {Command} not found", value);
        });
        Add(application, "debug-reader-more", null, _ => (Current().DebugCommandBar ?? throw new InvalidOperationException("The reader has no command bar.")).OpenMore());
        Add(application, "debug-reader-labels", "s", value =>
            (Current().DebugCommandBar ?? throw new InvalidOperationException("The reader has no command bar.")).ShowsLabels = value == "on");
    }
}
#endif
