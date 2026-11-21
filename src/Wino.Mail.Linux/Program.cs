namespace Wino.Mail.Linux;

internal static class Program
{
    /// <summary>The GApplication id, the name of the .desktop file and the application attribute of Secret Service items. Never change it.</summary>
    public const string ApplicationId = "app.winomail.Mail";

    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args, out var forwarded);
        Wino.Presentation.Linux.WinoIcons.Register(Path.Combine(AppContext.BaseDirectory, "Fonts"));
        Wino.Presentation.Linux.WinoPresentation.Initialize();
        var application = Adw.Application.New(ApplicationId, Gio.ApplicationFlags.HandlesOpen);
        _ = new LinuxApplication(application, options);

        // GApplication expects the program name first; everything after it is opened as a file or an address.
        return application.RunWithSynchronizationContext([Environment.GetCommandLineArgs()[0], .. forwarded]);
    }
}

/// <summary>Options of this head. GApplication rejects options it does not know, so they are taken out before it runs.</summary>
internal sealed record LaunchOptions(string? LabLeasePath, string? LabServer, bool DebugActions, bool LabEnableDav = false)
{
    public static LaunchOptions Parse(string[] args, out string[] forwarded)
    {
        string? lease = null, server = null;
        var debugActions = false;
        var enableDav = false;
        var rest = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--lab-lease" && index + 1 < args.Length) lease = args[++index];
            else if (args[index] == "--lab-server" && index + 1 < args.Length) server = args[++index];
            else if (args[index] == "--debug-actions") debugActions = true;
            else if (args[index] == "--lab-enable-dav") enableDav = true;
            else rest.Add(args[index]);
        }
        forwarded = [.. rest];
        return new LaunchOptions(lease, server, debugActions, enableDav);
    }
}
