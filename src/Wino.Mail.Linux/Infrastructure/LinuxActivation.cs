using System.Diagnostics.CodeAnalysis;
using Wino.Core.Activation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Turns what GApplication opened (an address or a file) into the shared activation kinds.</summary>
internal static class LinuxActivation
{
    public static ActivationItem? Classify(Gio.File file)
    {
        var uri = file.GetUri();
        if (string.IsNullOrEmpty(uri)) return null;
        // GLib keeps an address of an unknown scheme as a dummy file and gives it back as "mailto:///to@host".
        if (uri.StartsWith("mailto:/", StringComparison.OrdinalIgnoreCase)) uri = "mailto:" + uri["mailto:".Length..].TrimStart('/');
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        Serilog.Log.Information("Activated with a {Scheme} address.", parsed.Scheme);
        if (file.GetPath() is { Length: > 0 } path)
            return new ActivationItem(ActivationUriClassifier.ClassifyFilePath(path), uri, new LocalActivationFile(path));
        return new ActivationItem(ActivationUriClassifier.Classify(parsed), uri);
    }

    private sealed class LocalActivationFile(string path) : IActivationFile
    {
        public string? Path => path;

        public IDisposable BeginAccess() => NoAccess.Instance;
    }

    private sealed class NoAccess : IDisposable
    {
        public static readonly NoAccess Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Desktop actions and the command line (<c>--mode</c>, <c>--compose</c>, <c>--new-event</c>) as head-local addresses:
    /// GApplication forwards only addresses and files to the running instance, and the shared classifier knows no mode.
    /// </summary>
    public const string CommandPrefix = "wino://linux/";

    public static string ModeCommand(string mode) => CommandPrefix + "mode/" + mode;

    public const string ComposeCommand = CommandPrefix + "compose";

    public const string NewEventCommand = CommandPrefix + "new-event";

    public static bool TryGetCommand(Gio.File file, [NotNullWhen(true)] out LinuxLaunchCommand? command)
    {
        command = null;
        var uri = file.GetUri();
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri[CommandPrefix.Length..].TrimEnd('/');
        command = path.ToLowerInvariant() switch
        {
            "compose" => new LinuxLaunchCommand(LinuxLaunchCommandKind.Compose, WinoApplicationMode.Mail),
            "new-event" => new LinuxLaunchCommand(LinuxLaunchCommandKind.NewEvent, WinoApplicationMode.Calendar),
            "mode/mail" => new LinuxLaunchCommand(LinuxLaunchCommandKind.Mode, WinoApplicationMode.Mail),
            "mode/calendar" => new LinuxLaunchCommand(LinuxLaunchCommandKind.Mode, WinoApplicationMode.Calendar),
            "mode/contacts" => new LinuxLaunchCommand(LinuxLaunchCommandKind.Mode, WinoApplicationMode.Contacts),
            "mode/todo" or "mode/tasks" => new LinuxLaunchCommand(LinuxLaunchCommandKind.Mode, WinoApplicationMode.Tasks),
            _ => null,
        };
        if (command is null) Serilog.Log.Warning("Unknown launch command {Command}.", path);
        return true;
    }

    /// <summary>A <c>wino://auth-complete</c> address: it goes to the waiting sign-in, never to activation routing.</summary>
    public static bool TryGetAuthCallback(Gio.File file, [NotNullWhen(true)] out Uri? callback)
    {
        callback = null;
        var uri = file.GetUri();
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (!WinoAccountExternalAuth.IsCallback(parsed)) return false;
        callback = parsed;
        return true;
    }
}

internal enum LinuxLaunchCommandKind
{
    Mode,
    Compose,
    NewEvent,
}

internal sealed record LinuxLaunchCommand(LinuxLaunchCommandKind Kind, WinoApplicationMode Mode);
