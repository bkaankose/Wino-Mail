#if DEBUG
using System.Text;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.Controls.Linux.Intelligence;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-intelligence-gallery</c> <c>s</c>: <c>'all'</c> (or <c>''</c>) opens the window <c>Intelligence gallery</c>
/// with the reader's intelligence header in every state, built from hand-made <see cref="WinoIntelligenceHeaderState"/>s
/// (the real control renders them; nothing reaches a presenter), plus the progress ring at two sizes; a state name opens
/// the window with that state only; <c>'list'</c> logs the names; <c>'close'</c> closes it; a path writes each header's
/// state and whether its rings tick. Header events are logged.
/// </summary>
internal static class IntelligenceGalleryDebugActions
{
    private const string WindowTitle = "Intelligence gallery";
    private static Adw.Window? _window;
    private static readonly List<(string Name, WinoIntelligenceHeader Header)> Headers = [];
    private static readonly List<WinoIntelligenceProgressRing> Rings = [];

    private static readonly string[] Names =
    [
        "not-processed", "queued", "processing", "processed", "processed-expanded", "summary-waiting", "summary-text",
        "translate-idle", "translate-busy", "translate-applied", "failed", "unavailable", "ring"
    ];

    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-intelligence-gallery", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            var argument = args.Parameter?.GetString(out var _) ?? string.Empty;
            try
            {
                if (argument == "close") Close();
                else if (argument == "list") Serilog.Log.Information("Debug action: intelligence gallery states {States}.", string.Join(", ", Names));
                else if (argument.Contains('/')) DebugActions.WriteText(argument, Describe());
                else Show(application, argument is "" or "all" ? null : argument);
                Serilog.Log.Information("Debug action debug-intelligence-gallery {Argument}: done", argument);
            }
            catch (Exception exception)
            {
                Serilog.Log.Error(exception, "Debug action debug-intelligence-gallery failed.");
            }
        };
        application.AddAction(action);
    }

    private static WinoIntelligenceHeaderState Base(string key) => WinoIntelligenceHeaderState.Empty with
    {
        ContentKey = key,
        IsSummaryAvailable = true,
        IsTranslateAvailable = true,
        IsProcessingAvailable = true,
        Languages = Languages,
        SelectedSourceLanguage = string.Empty,
        SelectedTargetLanguage = "de-DE",
    };

    private static readonly WinoIntelligenceLanguageOption[] Languages =
    [
        new(string.Empty, "Detect language"), new("en-US", "English"), new("de-DE", "German"), new("tr-TR", "Turkish"), new("ja-JP", "Japanese"),
    ];

    private static readonly WinoIntelligenceTile[] Tiles =
    [
        new(WinoIntelligenceTileKind.SmartLabel, string.Empty, "Needs reply", "Needs reply"),
    ];

    private static (WinoIntelligenceHeaderState State, Action<WinoIntelligenceHeader>? Arrange) Build(string name)
    {
        var processed = Base(name) with
        {
            ProcessingState = WinoIntelligenceProcessingState.Processed,
            BriefingFactText = "Carol asks for the signed contract by Friday",
            DeadlineText = "Friday, 28 November",
            DeadlineDetailText = "Contract signature",
            VerificationCode = "482913",
            Tiles = Tiles,
        };
        const string summary = "Carol shares the revised contract and asks you to sign and return it by Friday. The payment terms moved to 45 days; legal approved the change. She also proposes a call on Monday at 10:00 to walk through the remaining open points.";
        return name switch
        {
            "not-processed" => (Base(name), null),
            "queued" => (Base(name) with { ProcessingState = WinoIntelligenceProcessingState.Queued }, null),
            "processing" => (Base(name) with { ProcessingState = WinoIntelligenceProcessingState.Processing }, null),
            "processed" => (processed, null),
            "processed-expanded" => (processed, static h => h.IsExpanded = true),
            "summary-waiting" => (processed with { SummaryState = WinoIntelligenceFeatureState.Busy }, static h => h.ShowSummary()),
            "summary-text" => (processed with { SummaryState = WinoIntelligenceFeatureState.Done, SummaryText = summary }, static h => h.ShowSummary()),
            "translate-idle" => (processed, static h => h.ShowTranslation()),
            "translate-busy" => (processed with { IsTranslationBusy = true, TranslationStatusText = "Translating to German…" }, static h => h.ShowTranslation()),
            "translate-applied" => (processed with { HasTranslationResult = true, IsTranslationApplied = true, TranslationStatusText = "Translated from English to German" }, static h => h.ShowTranslation()),
            "failed" => (Base(name) with { ProcessingState = WinoIntelligenceProcessingState.Failed }, static h => h.IsExpanded = true),
            "unavailable" => (WinoIntelligenceHeaderState.Empty with { ContentKey = name, ProcessingState = WinoIntelligenceProcessingState.Unavailable, IsProcessingAvailable = true }, null),
            _ => throw new ArgumentException($"Unknown intelligence gallery state '{name}'.")
        };
    }

    private static void Show(Adw.Application application, string? only)
    {
        Close();
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        column.SetMarginTop(12);
        column.SetMarginBottom(12);
        column.SetMarginStart(12);
        column.SetMarginEnd(12);
        foreach (var name in Names)
        {
            if (only is not null && only != name) continue;
            var caption = Gtk.Label.New(name);
            caption.AddCssClass("caption");
            caption.AddCssClass("dim-label");
            caption.SetXalign(0);
            column.Append(caption);
            if (name == "ring")
            {
                var rings = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
                foreach (var size in (int[])[24, 32])
                {
                    var ring = new WinoIntelligenceProgressRing(size) { Active = true };
                    Rings.Add(ring);
                    rings.Append(ring.Root);
                }
                column.Append(rings);
                continue;
            }
            var header = new WinoIntelligenceHeader();
            var (state, arrange) = Build(name);
            header.Apply(state);
            arrange?.Invoke(header);
            var captured = name;
            header.ProcessRequested += () => Log(captured, "process");
            header.CopyCodeRequested += () => Log(captured, "copy-code");
            header.SummaryRequested += () => Log(captured, "summary");
            header.SummaryCancelRequested += () => Log(captured, "summary-cancel");
            header.SummaryCopied += () => Log(captured, "summary-copied");
            header.TranslateRequested += () => Log(captured, "translate");
            header.TranslationCancelRequested += () => Log(captured, "translate-cancel");
            header.SourceLanguageChanged += code => Log(captured, "source " + code);
            header.TargetLanguageChanged += code => Log(captured, "target " + code);
            Headers.Add((name, header));
            column.Append(header.Root);
        }
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetChild(column);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(scroller);
        var window = Adw.Window.New();
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(640, 736);
        window.SetContent(toolbar);
        window.SetApplication(application);
        window.OnCloseRequest += (_, _) =>
        {
            Release();
            return false;
        };
        _window = window;
        window.Present();
    }

    private static void Log(string name, string what) => Serilog.Log.Information("Debug action: intelligence gallery {State} raised {Event}.", name, what);

    private static string Describe()
    {
        var text = new StringBuilder();
        foreach (var (name, header) in Headers) text.Append(name).Append(": ").AppendLine(header.Describe());
        text.Append("rings=").AppendLine(string.Join(",", Rings.Select(static r => r.IsTicking)));
        return text.ToString();
    }

    private static void Close()
    {
        var window = _window;
        Release();
        window?.Destroy();
    }

    private static void Release()
    {
        _window = null;
        foreach (var (_, header) in Headers) header.Dispose();
        Headers.Clear();
        foreach (var ring in Rings) ring.Dispose();
        Rings.Clear();
    }
}
#endif
