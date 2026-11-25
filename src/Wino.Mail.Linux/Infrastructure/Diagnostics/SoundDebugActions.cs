#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Linux.Native;
using Wino.Platform.Linux.Notifications;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-sound</c> <c>as</c> (PL-06): play &lt;id&gt; (default and none too; waits for libcanberra's finish code),
/// resolve mail|calendar|task, set mail|calendar|task &lt;id&gt;, choices, task-complete (the To Do completion sound),
/// launch-folder &lt;path&gt;, state (library, plays started, last finish code, last error). One log line
/// <c>Debug action debug-sound &lt;verb&gt;: &lt;result&gt;</c>; a last argument ending in .txt also receives it.
/// </summary>
internal static class SoundDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-sound", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var rest = arguments.Length > 1 ? arguments[1..(path is null ? arguments.Length : arguments.Length - 1)] : [];
        string result;
        try
        {
            var provider = services() ?? throw new InvalidOperationException("no services");
            var sounds = provider.GetRequiredService<LinuxNotificationSounds>();
            result = verb switch
            {
                "play" => await PlayAsync(() => sounds.Play(rest.Length > 0 ? rest[0] : LinuxNotificationSounds.DefaultSound, NotificationSoundKind.Mail)),
                "task-complete" => await PlayAsync(() =>
                {
                    provider.GetRequiredService<ITaskCompletionSound>().Play();
                    return true;
                }),
                "resolve" => sounds.Resolve(Kind(rest)),
                "set" => Set(sounds, rest),
                "choices" => string.Join(", ", sounds.Choices.Select(static choice => $"{choice.Id}={choice.DisplayName}")),
                "launch-folder" => (await provider.GetRequiredService<IExternalLauncher>().LaunchFolderAsync(string.Join(' ', rest))).Status.ToString(),
                "state" => State(),
                _ => "verbs: play, task-complete, resolve, set, choices, launch-folder, state",
            };
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
            Serilog.Log.Error(exception, "Debug action debug-sound {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-sound {Verb}: {Result}", verb, result);
        if (path is not null) DebugActions.WriteText(path, result);
    }

    private static NotificationSoundKind Kind(string[] rest) => rest.Length > 0 && rest[0] == "calendar" ? NotificationSoundKind.Calendar
        : rest.Length > 0 && rest[0] == "task" ? NotificationSoundKind.Task : NotificationSoundKind.Mail;

    private static string Set(LinuxNotificationSounds sounds, string[] rest)
    {
        var kind = Kind(rest);
        sounds.SetSound(kind, rest.Length > 1 ? rest[1] : string.Empty);
        return $"{kind}={sounds.Resolve(kind)}";
    }

    // The finish callback reports 0 when the sound played to the end; theme sounds are under three seconds.
    private static async Task<string> PlayAsync(Func<bool> play)
    {
        var before = CanberraState.LastId;
        var accepted = await Task.Run(play);
        for (var waited = 0; waited < 50 && CanberraState.FinishedAfter(before) is null; waited++) await Task.Delay(100);
        return $"accepted={accepted} finished={CanberraState.FinishedAfter(before)?.ToString() ?? "-"} {State()}";
    }

    private static string State()
        => $"unavailable={CanberraState.IsUnavailable} started={CanberraState.Started} lastFinished={CanberraState.LastFinished?.ToString() ?? "-"} lastError={CanberraState.LastError ?? "-"}";
}
#endif
