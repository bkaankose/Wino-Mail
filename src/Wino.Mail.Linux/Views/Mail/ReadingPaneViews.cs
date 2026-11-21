using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>Reader state "no message": glyph, title, message, the actions, a spinner and the shortcut hints.</summary>
internal sealed class MailIdleContent : IDisposable
{
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _message = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _hints = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Button _action = Gtk.Button.New();
    private readonly Gtk.Button _secondary = Gtk.Button.New();
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly IDisposable _actionClicked;
    private readonly IDisposable _secondaryClicked;
    private (string Accelerator, string Title)[] _hintValues = [];

    public MailIdleContent()
    {
        var glyph = new WinoIcon(WinoIconGlyph.Mail, 56, WinoIconMode.Mono).Root;
        glyph.AddCssClass("dim-label");
        _title.AddCssClass("title-3");
        _title.SetJustify(Gtk.Justification.Center);
        _title.SetWrap(true);
        _message.AddCssClass("dim-label");
        _message.SetJustify(Gtk.Justification.Center);
        _message.SetWrap(true);
        _hints.SetHalign(Gtk.Align.Center);
        _hints.SetVisible(false);
        _spinner.SetSizeRequest(32, 32);
        _spinner.SetHalign(Gtk.Align.Center);
        _spinner.SetVisible(false);
        foreach (var button in new[] { _action, _secondary })
        {
            button.AddCssClass("pill");
            button.SetHalign(Gtk.Align.Center);
            button.SetVisible(false);
        }
        _action.AddCssClass("suggested-action");
        _actionClicked = GtkSignals.OnClicked(_action, () => ActionInvoked?.Invoke());
        _secondaryClicked = GtkSignals.OnClicked(_secondary, () => SecondaryInvoked?.Invoke());

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        column.SetValign(Gtk.Align.Center);
        column.SetMarginTop(24);
        column.SetMarginBottom(24);
        column.SetMarginStart(24);
        column.SetMarginEnd(24);
        column.Append(glyph);
        column.Append(_title);
        column.Append(_message);
        column.Append(_spinner);
        column.Append(_hints);
        column.Append(_action);
        column.Append(_secondary);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(360);
        clamp.SetChild(column);
        clamp.SetVexpand(true);
        Root = clamp;
    }

    public Gtk.Widget Root { get; }
    public event Action? ActionInvoked;
    public event Action? SecondaryInvoked;
    public string Title => _title.GetText();

    public void SetContent(string title, string? message, string? actionTitle, string? secondaryTitle = null, bool loading = false)
    {
        _title.SetText(title);
        _message.SetText(message ?? string.Empty);
        _message.SetVisible(!string.IsNullOrEmpty(message));
        SetButton(_action, actionTitle);
        SetButton(_secondary, secondaryTitle);
        _spinner.SetVisible(loading);
    }

    public void SetHints((string Accelerator, string Title)[] hints)
    {
        if (hints.AsSpan().SequenceEqual(_hintValues)) return;
        _hintValues = hints;
        while (_hints.GetFirstChild() is { } child) _hints.Remove(child);
        foreach (var (accelerator, title) in hints)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.Append(Adw.ShortcutLabel.New(accelerator));
            var label = Gtk.Label.New(title);
            label.AddCssClass("dim-label");
            row.Append(label);
            _hints.Append(row);
        }
        _hints.SetVisible(hints.Length > 0);
    }

    public string Describe()
        => $"title=\"{_title.GetText()}\" message=\"{_message.GetText()}\" action={(_action.GetVisible() ? _action.GetLabel() : "-")} secondary={(_secondary.GetVisible() ? _secondary.GetLabel() : "-")} loading={_spinner.GetVisible()} hints=[{string.Join(", ", _hintValues.Select(static hint => $"{hint.Accelerator} {hint.Title}"))}]";

    public void Dispose()
    {
        _actionClicked.Dispose();
        _secondaryClicked.Dispose();
        ActionInvoked = null;
        SecondaryInvoked = null;
    }

    private static void SetButton(Gtk.Button button, string? title)
    {
        var visible = !string.IsNullOrEmpty(title);
        button.SetVisible(visible);
        if (!visible) return;
        button.SetLabel(title ?? string.Empty);
        WinoA11y.Rename(button, title ?? string.Empty);
    }
}

/// <summary>Reader state "loading": covers the reader below the subject row with the pane background.</summary>
internal sealed class MailLoadingView : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Box _content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly Gtk.ProgressBar _progress = Gtk.ProgressBar.New();
    private IDisposable? _delay;

    public MailLoadingView()
    {
        _root.AddCssClass(WinoCssClass.ReaderLoading);
        _root.SetHalign(Gtk.Align.Fill);
        _root.SetValign(Gtk.Align.Fill);
        _root.SetVisible(false);
        _spinner.SetSizeRequest(32, 32);
        _spinner.SetHalign(Gtk.Align.Center);
        var label = Gtk.Label.New(Translator.MacOS_Reader_DownloadingMessage);
        label.AddCssClass("dim-label");
        label.SetWrap(true);
        label.SetJustify(Gtk.Justification.Center);
        _progress.SetSizeRequest(200, -1);
        _progress.SetHalign(Gtk.Align.Center);
        _progress.SetVisible(false);
        _content.SetVexpand(true);
        _content.SetValign(Gtk.Align.Center);
        _content.SetHalign(Gtk.Align.Center);
        _content.SetMarginStart(24);
        _content.SetMarginEnd(24);
        _content.Append(_spinner);
        _content.Append(label);
        _content.Append(_progress);
        _content.SetVisible(false);
        _root.Append(_content);
        WinoA11y.Label(_root, Translator.MacOS_Reader_DownloadingMessage);
    }

    public Gtk.Widget Root => _root;
    public bool IsActive => _root.GetVisible();

    public void SetActive(bool active)
    {
        _delay?.Dispose();
        _delay = null;
        _root.SetVisible(active);
        _content.SetVisible(false);
        // A cached message renders within a few frames; the spinner row only appears for a real download.
        if (active) _delay = MainLoop.Delay(200, () => _content.SetVisible(true));
    }

    public void SetProgress(bool indeterminate, double percentage)
    {
        var determinate = !indeterminate && percentage > 0;
        _progress.SetVisible(determinate);
        if (determinate) _progress.SetFraction(Math.Clamp(percentage / 100.0, 0, 1));
    }

    public void Dispose()
    {
        _delay?.Dispose();
        _delay = null;
    }
}
