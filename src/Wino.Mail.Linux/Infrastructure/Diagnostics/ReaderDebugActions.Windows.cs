#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-popout</c> (pops the pane's reader out), <c>debug-reader-windows</c> <c>s</c> path (the reader
/// windows), <c>debug-reader-window-close</c> <c>s</c> key, <c>debug-reader-window-screenshot</c> <c>s</c> <c>key|path</c>, <c>debug-reader-window-select</c> <c>s</c> key (points the
/// other <c>debug-reader-*</c> actions at that window's reader), <c>debug-eml-open</c> <c>s</c> path (the activation path
/// GApplication's open signal takes).
/// </summary>
internal static partial class ReaderDebugActions
{
    private static readonly Dictionary<string, WeakReference<MailReaderView>> WindowReaders = new(StringComparer.Ordinal);

    static partial void RegisterWindows(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-popout", null, _ =>
        {
            var view = Current();
            var registry = Required<ReaderWindowRegistry>(services);
            Observe(PopOutAsync(view, registry));
        });
        Add(application, "debug-reader-windows", "s", value => Write(value, string.Join('\n', Required<ReaderWindowRegistry>(services).Describe()) + "\n"));
        Add(application, "debug-reader-window-close", "s", value =>
            (Required<ReaderWindowRegistry>(services).Find(value) ?? throw new InvalidOperationException($"No window '{value}'.")).Close());
        Add(application, "debug-reader-window-screenshot", "s", value =>
        {
            var parts = value.Split('|', 2);
            if (parts.Length != 2) throw new ArgumentException("Expected 'key|path'.");
            var window = Required<ReaderWindowRegistry>(services).Find(parts[0]) ?? throw new InvalidOperationException($"No window '{parts[0]}'.");
            DebugActions.Capture(window.Window, parts[1]);
        });
        Add(application, "debug-reader-window-select", "s", value =>
        {
            if (!WindowReaders.TryGetValue(value, out var reference) || !reference.TryGetTarget(out var view))
                throw new InvalidOperationException($"No reader for '{value}'.");
            ReaderDiagnostics.Current = view;
        });
        Add(application, "debug-eml-open", "s", value =>
        {
            var item = LinuxActivation.Classify(Gio.FileHelper.NewForPath(value)) ?? throw new ArgumentException($"'{value}' is not an activation.");
            Observe(Required<ActivationRouter>(services).HandleAsync([item]));
            Observe(RememberFileReaderAsync(Required<ReaderWindowRegistry>(services), ReaderWindowRegistry.FileKey(value)));
        });
    }

    private static async Task PopOutAsync(MailReaderView view, ReaderWindowRegistry registry)
    {
        var result = await view.PopOutAsync();
        if (view.WindowKey is { } key) WindowReaders[key] = new WeakReference<MailReaderView>(view);
        Serilog.Log.Information("Debug action: reader popout {Result} windows=[{Windows}]", result, string.Join(" | ", registry.Describe()));
    }

    private static async Task RememberFileReaderAsync(ReaderWindowRegistry registry, string key)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            if (registry.Find(key) is not null && ReaderDiagnostics.Current is { } view && view.WindowKey == key)
            {
                WindowReaders[key] = new WeakReference<MailReaderView>(view);
                return;
            }
        }
    }

    private static T Required<T>(Func<IServiceProvider?> services) where T : notnull
        => (services() ?? throw new InvalidOperationException("No services.")).GetRequiredService<T>();
}
#endif
