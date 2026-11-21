#if DEBUG
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

internal static class ReaderDiagnostics
{
    private static WeakReference<MailReaderView>? _current;

    public static MailReaderView? Current
    {
        get => _current is not null && _current.TryGetTarget(out var view) ? view : null;
        set => _current = value is null ? null : new WeakReference<MailReaderView>(value);
    }
}

/// <summary>
/// Reader render time: selection (<c>ActiveMailItemChangedEvent</c>) -> navigation -> render call -> render returned ->
/// first paint, one animation frame after <c>reader.js</c> made <c>#wino-reader</c> visible.
/// </summary>
internal static class ReaderRenderTiming
{
    private const string Probe =
        "(() => { const r = document.getElementById('wino-reader'); if (!r || r.style.visibility !== 'visible') return '';" +
        " if (!window.__winoPaint) { window.__winoPaint = 'pending'; requestAnimationFrame(() => { window.__winoPaint = 'done'; }); }" +
        " return window.__winoPaint === 'done' ? '1' : ''; })()";

    private sealed class Sample
    {
        public long Origin;
        public long Navigated;
        public long RenderCalled;
        public long Rendered;
        public long Painted;
    }

    private static readonly object Gate = new();
    private static readonly List<Sample> Samples = [];
    private static long _selected;
    private static Sample? _current;

    public static int Count { get { lock (Gate) return Samples.Count; } }

    public static void Selected()
    {
        lock (Gate) _selected = Stopwatch.GetTimestamp();
    }

    public static void Navigated(MailReaderView view)
    {
        var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            var origin = _selected != 0 && Stopwatch.GetElapsedTime(_selected, now) < TimeSpan.FromSeconds(2) ? _selected : now;
            _selected = 0;
            _current = new Sample { Origin = origin, Navigated = now };
        }
    }

    public static void RenderCalled()
    {
        lock (Gate)
        {
            if (_current is { RenderCalled: 0 } sample) sample.RenderCalled = Stopwatch.GetTimestamp();
        }
    }

    public static async Task WatchPaintAsync(WebKit.WebView view)
    {
        Sample? sample;
        lock (Gate)
        {
            sample = _current;
            if (sample is null || sample.RenderCalled == 0 || sample.Rendered != 0) return;
            sample.Rendered = Stopwatch.GetTimestamp();
        }
        try
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                await Task.Delay(5);
                lock (Gate) if (!ReferenceEquals(_current, sample)) return;
                using var result = await view.EvaluateJavascriptAsync(Probe);
                if (result.ToString() != "1") continue;
                lock (Gate)
                {
                    if (!ReferenceEquals(_current, sample)) return;
                    sample.Painted = Stopwatch.GetTimestamp();
                    Samples.Add(sample);
                    if (Samples.Count > 50) Samples.RemoveAt(0);
                    _current = null;
                }
                return;
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Samples.Clear();
            _current = null;
        }
    }

    public static string Report()
    {
        lock (Gate)
        {
            var text = new StringBuilder("# nav_ms render_call_ms rendered_ms painted_ms from the selection (resolution: one 5 ms poll plus one frame)\n");
            foreach (var sample in Samples)
                text.Append($"{Ms(sample, sample.Navigated):F1} {Ms(sample, sample.RenderCalled):F1} {Ms(sample, sample.Rendered):F1} {Ms(sample, sample.Painted):F1}\n");
            var painted = Samples.Select(sample => Ms(sample, sample.Painted)).Order().ToArray();
            if (painted.Length > 0)
                text.Append($"samples={painted.Length} median={Percentile(painted, 0.5):F1} p95={Percentile(painted, 0.95):F1}\n");
            return text.ToString();
        }
    }

    private static double Ms(Sample sample, long stamp) => stamp == 0 ? 0 : Stopwatch.GetElapsedTime(sample.Origin, stamp).TotalMilliseconds;

    private static double Percentile(double[] sorted, double fraction)
    {
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Reader checks: <c>debug-reader-state</c> <c>s</c> path, <c>debug-reader-timing</c> <c>s</c> path,
/// <c>debug-reader-timing-reset</c>, <c>debug-reader-crash</c>, <c>debug-reader-card</c>, <c>debug-reader-expand</c>
/// <c>i</c> row, <c>debug-reader-cover</c> <c>s</c> text (empty removes it), <c>debug-clipboard-text</c> <c>s</c> path,
/// <c>debug-reader-select</c> <c>i</c> list position (selects it and starts a timing sample at the selection).
/// </summary>
internal static partial class ReaderDebugActions
{
    private sealed class SelectionRecipient
    {
    }

    private static readonly SelectionRecipient Recipient = new();
    private static bool _subscribed;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-state", "s", value => Write(value, Current().Describe()));
        Add(application, "debug-reader-timing", "s", value => Write(value, ReaderRenderTiming.Report()));
        Add(application, "debug-reader-timing-reset", null, _ => ReaderRenderTiming.Reset());
        Add(application, "debug-reader-crash", null, _ => (Current().DebugWebView ?? throw new InvalidOperationException("The reader has no web view.")).TerminateWebProcess());
        Add(application, "debug-reader-card", null, _ => Current().DebugShowSenderCard());
        Add(application, "debug-reader-expand", "i", value => Current().DebugHeader.Recipients.Expand(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
        Add(application, "debug-reader-cover", "s", value =>
        {
            var navigation = (services() ?? throw new InvalidOperationException("No services.")).GetRequiredService<GtkNavigationService>();
            navigation.SetReadingPaneCover(value.Length == 0 ? null : Gtk.Label.New(value));
        });
        Add(application, "debug-clipboard-text", "s", value => Observe(ClipboardAsync(value)));
        Add(application, "debug-reader-select", "i", value =>
        {
            var list = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
            ReaderRenderTiming.Selected();
            list.DebugSelectPositions([uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture)]);
        });
        RegisterCommands(application, services);
        RegisterAttachments(application, services);

        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 500, () =>
        {
            if (_subscribed) return false;
            if (services() is not { } provider) return true;
            _subscribed = true;
            provider.GetRequiredService<IMessenger>().Register<SelectionRecipient, ActiveMailItemChangedEvent>(Recipient, static (_, message) =>
            {
                if (message.SelectedMailItemViewModel is not null) ReaderRenderTiming.Selected();
            });
            return false;
        });
    }

    static partial void RegisterCommands(Adw.Application application, Func<IServiceProvider?> services);
    static partial void RegisterAttachments(Adw.Application application, Func<IServiceProvider?> services);

    private static MailReaderView Current() => ReaderDiagnostics.Current ?? throw new InvalidOperationException("No reader is shown.");

    private static async Task ClipboardAsync(string path)
    {
        var display = Gdk.Display.GetDefault() ?? throw new InvalidOperationException("No display.");
        var text = await display.GetClipboard().ReadTextAsync();
        Write(path, text ?? string.Empty);
    }

    private static void Add(Adw.Application application, string name, string? type, Action<string> run)
    {
        var action = Gio.SimpleAction.New(name, type is null ? null : GLib.VariantType.New(type));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var value = type switch
                {
                    null => string.Empty,
                    "i" => (args.Parameter?.GetInt32() ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => args.Parameter?.GetString(out var _) ?? string.Empty,
                };
                run(value);
                Serilog.Log.Information("Debug action {Action} done.", name);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Reader debug action failed."); }
    }

    private static void Write(string path, string text)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("An output path is required.");
        DebugActions.WriteText(path, text);
    }
}
#endif
