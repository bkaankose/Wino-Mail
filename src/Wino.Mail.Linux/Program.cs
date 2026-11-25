namespace Wino.Mail.Linux;

internal static class Program
{
    /// <summary>The GApplication id, the name of the .desktop file and the application attribute of Secret Service items. Never change it.</summary>
    public const string ApplicationId = "app.winomail.Mail";

    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args, out var forwarded);
        Infrastructure.LinuxLanguage.Prepare();
        Wino.Presentation.Linux.WinoIcons.Register(Path.Combine(AppContext.BaseDirectory, "Fonts"));
        Wino.Presentation.Linux.WinoPresentation.Initialize();
        Wino.Presentation.Linux.GtkSignals.HandlerFailed = exception => Serilog.Log.Error(exception, "A signal handler failed.");
        // Windows that are not added to the application (Settings, the embedded sign-in browser) take their Wayland
        // app id from the program name, which would otherwise be the executable's name.
        GLib.Functions.SetPrgname(ApplicationId);
        var application = Adw.Application.New(ApplicationId, Gio.ApplicationFlags.HandlesOpen);
        _ = new LinuxApplication(application, options);

        // GApplication expects the program name first; everything after it is opened as a file or an address.
        return application.RunWithSynchronizationContext([Environment.GetCommandLineArgs()[0], .. forwarded]);
    }
}

/// <summary>
/// Options of this head. GApplication rejects options it does not know, so they are taken out before it runs; the ones a
/// running instance must act on become head-local addresses that it forwards (<see cref="Infrastructure.LinuxActivation.CommandPrefix"/>).
/// </summary>
internal sealed record LaunchOptions(string? LabLeasePath, string? LabServer, bool DebugActions, bool LabEnableDav = false, bool Background = false)
{
    public static LaunchOptions Parse(string[] args, out string[] forwarded)
    {
        string? lease = null, server = null;
        var debugActions = false;
        var enableDav = false;
        var background = false;
        var rest = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--lab-lease" && index + 1 < args.Length) lease = args[++index];
            else if (args[index] == "--lab-server" && index + 1 < args.Length) server = args[++index];
            else if (args[index] == "--debug-actions") debugActions = true;
            else if (args[index] == "--lab-enable-dav") enableDav = true;
            else if (args[index] == Wino.Platform.Linux.Services.LinuxStartupIntegrationService.BackgroundOption) background = true;
            else if (args[index] == "--mode" && index + 1 < args.Length) rest.Add(Infrastructure.LinuxActivation.ModeCommand(args[++index]));
            else if (args[index] == "--compose") rest.Add(Infrastructure.LinuxActivation.ComposeCommand);
            else if (args[index] == "--new-event") rest.Add(Infrastructure.LinuxActivation.NewEventCommand);
            else rest.Add(args[index]);
        }
        forwarded = [.. rest];
        return new LaunchOptions(lease, server, debugActions, enableDav, background);
    }
}
