using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Dialogs;

internal sealed record ListPickerRow(string Title)
{
    public string? Subtitle { get; init; }
    public WinoIconGlyph Glyph { get; init; } = WinoIconGlyph.None;
    public string? GlyphTintHex { get; init; }
    public string? ColorDotHex { get; init; }

    /// <summary>Built on the main thread; wins over <see cref="Glyph"/> and <see cref="ColorDotHex"/>.</summary>
    public Func<Gtk.Widget?>? Prefix { get; init; }

    /// <summary>Not selectable; groups the rows after it.</summary>
    public bool IsHeading { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? DisabledReason { get; init; }
}

internal sealed record ListPickerRequest(string Title, ListPickerRow[] Rows, string ConfirmText)
{
    public string? Description { get; init; }
    public int InitialIndex { get; init; } = -1;

    /// <summary>Null: shown with more than eight selectable rows.</summary>
    public bool? ShowSearch { get; init; }
    public string? FooterLinkText { get; init; }
}

/// <summary>Index is -1 when the dialog was dismissed; the footer link closes the dialog without a choice.</summary>
internal readonly record struct ListPickerResult(int Index, bool FooterLinkActivated)
{
    public static ListPickerResult Dismissed { get; } = new(-1, false);
}

/// <summary>
/// A single-choice list in a <see cref="FormDialog"/>: optional search, headings, disabled rows with a reason and an
/// optional footer link. Select a row and press the primary button, or activate a row.
/// </summary>
internal static class ListPickerDialog
{
    private const int SearchThreshold = 8;

#if DEBUG
    private static readonly List<Picker> _open = [];
    private static Picker? Top => _open.Count > 0 ? _open[^1] : null;
#endif

    /// <summary>Main thread only.</summary>
    public static async Task<ListPickerResult> PresentAsync(Gtk.Widget owner, ListPickerRequest request)
    {
        var picker = new Picker(request);
#if DEBUG
        _open.Add(picker);
#endif
        try { return await picker.PresentAsync(owner); }
        finally
        {
#if DEBUG
            _open.Remove(picker);
#endif
        }
    }

#if DEBUG
    internal static string? DebugDescribeOpen() => Top?.Describe();

    internal static bool DebugChoose(int index) => Top?.Choose(index) == true;

    internal static bool DebugSearch(string text)
    {
        if (Top is not { } open) return false;
        open.Search(text);
        return true;
    }
#endif

    private sealed class Picker
    {
        private readonly ListPickerRequest _request;
        private readonly FormDialog _form;
        private readonly Gtk.ListBox _list = Gtk.ListBox.New();
        private readonly Gtk.SearchEntry? _search;
        private readonly List<(ListPickerRow Row, Gtk.ListBoxRow Widget, int Index)> _rows = [];
        private int _selected = -1;
        private bool _footer;

        public Picker(ListPickerRequest request)
        {
            _request = request;
            _form = new FormDialog(request.Title, request.Description, request.ConfirmText) { ContentWidth = 420 };

            var selectable = request.Rows.Count(row => !row.IsHeading && row.IsEnabled);
            if (request.ShowSearch ?? selectable > SearchThreshold)
            {
                _search = Gtk.SearchEntry.New();
                _search.SetPlaceholderText(Translator.SearchBarPlaceholder);
                WinoA11y.Label(_search, Translator.SearchBarPlaceholder);
                _form.Add(_search);
                _form.Own(GtkSignals.OnChanged(_search, Filter));
                _form.SetInitialFocus(_search);
            }

            _list.AddCssClass("boxed-list");
            _list.SetSelectionMode(Gtk.SelectionMode.Single);
            _list.SetActivateOnSingleClick(false);
            WinoA11y.Label(_list, request.Title);
            var scroll = Gtk.ScrolledWindow.New();
            scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroll.SetPropagateNaturalHeight(true);
            scroll.SetMinContentHeight(120);
            scroll.SetMaxContentHeight(360);
            scroll.SetChild(_list);
            _form.Add(scroll);

            for (var index = 0; index < request.Rows.Length; index++)
            {
                var row = request.Rows[index];
                var widget = row.IsHeading ? Heading(row) : Row(row);
                _list.Append(widget);
                _rows.Add((row, widget, index));
            }

            if (!string.IsNullOrEmpty(request.FooterLinkText))
            {
                var link = Gtk.Button.NewWithLabel(request.FooterLinkText);
                link.AddCssClass("flat");
                link.AddCssClass("accent");
                link.SetHalign(Gtk.Align.Start);
                _form.Add(link);
                _form.Own(GtkSignals.OnClicked(link, () =>
                {
                    _footer = true;
                    _form.Close(false);
                }));
            }

            _form.CanConfirm = () => _selected >= 0;
            _form.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowSelectedSignalArgs>(
                h => _list.OnRowSelected += h, h => _list.OnRowSelected -= h, (_, args) => Selected(args.Row)));
            _form.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowActivatedSignalArgs>(
                h => _list.OnRowActivated += h, h => _list.OnRowActivated -= h, (_, args) => Activated(args.Row)));

            if (request.InitialIndex >= 0 && request.InitialIndex < _rows.Count && Selectable(_rows[request.InitialIndex].Row))
            {
                var initial = _rows[request.InitialIndex].Widget;
                _list.SelectRow(initial);
                if (_search is null) _form.SetInitialFocus(initial);
            }
        }

        public async Task<ListPickerResult> PresentAsync(Gtk.Widget owner)
        {
            var confirmed = await _form.PresentAsync(owner);
            if (_footer) return new ListPickerResult(-1, true);
            return confirmed && _selected >= 0 ? new ListPickerResult(_selected, false) : ListPickerResult.Dismissed;
        }

        private static bool Selectable(ListPickerRow row) => !row.IsHeading && row.IsEnabled;

        private static Gtk.ListBoxRow Heading(ListPickerRow row)
        {
            var label = Gtk.Label.New(null);
            label.SetText(row.Title);
            label.AddCssClass("caption-heading");
            label.AddCssClass("dim-label");
            label.SetXalign(0);
            label.SetMarginTop(10);
            label.SetMarginBottom(4);
            label.SetMarginStart(12);
            label.SetMarginEnd(12);
            var heading = Gtk.ListBoxRow.New();
            heading.SetChild(label);
            heading.SetActivatable(false);
            heading.SetSelectable(false);
            heading.SetCanFocus(false);
            return heading;
        }

        private static Gtk.ListBoxRow Row(ListPickerRow row)
        {
            var widget = Adw.ActionRow.New();
            widget.SetUseMarkup(false);
            widget.SetTitle(row.Title);
            if (!string.IsNullOrEmpty(row.Subtitle)) widget.SetSubtitle(row.Subtitle);
            widget.SetTitleLines(1);
            widget.SetSubtitleLines(1);
            widget.SetActivatable(row.IsEnabled);
            widget.SetSelectable(row.IsEnabled);
            widget.SetSensitive(row.IsEnabled);

            if (row.Prefix?.Invoke() is { } prefix) widget.AddPrefix(prefix);
            else if (WinoTint.FillClassFor(row.ColorDotHex, null) is { } fill)
            {
                var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
                dot.AddCssClass(WinoCssClass.ColorDot);
                dot.AddCssClass(fill);
                dot.SetValign(Gtk.Align.Center);
                dot.SetHalign(Gtk.Align.Center);
                widget.AddPrefix(dot);
            }
            else if (row.Glyph != WinoIconGlyph.None)
            {
                var glyph = WinoIcons.Label(row.Glyph, 16);
                if (!string.IsNullOrEmpty(row.GlyphTintHex)) WinoIcons.SetGlyph(glyph, WinoIcons.Glyph(row.Glyph), row.GlyphTintHex);
                widget.AddPrefix(glyph);
            }

            if (!row.IsEnabled && !string.IsNullOrEmpty(row.DisabledReason))
            {
                var reason = Gtk.Label.New(row.DisabledReason);
                reason.AddCssClass("caption");
                reason.AddCssClass("dim-label");
                widget.AddSuffix(reason);
            }
            return widget;
        }

        private int IndexOf(Gtk.ListBoxRow? widget)
        {
            if (widget is null) return -1;
            foreach (var entry in _rows)
            {
                if (ReferenceEquals(entry.Widget, widget) || entry.Widget.Handle.DangerousGetHandle() == widget.Handle.DangerousGetHandle())
                    return Selectable(entry.Row) && entry.Widget.GetVisible() ? entry.Index : -1;
            }
            return -1;
        }

        private void Selected(Gtk.ListBoxRow? row)
        {
            _selected = IndexOf(row);
            _form.Refresh();
        }

        private void Activated(Gtk.ListBoxRow row)
        {
            var index = IndexOf(row);
            if (index < 0) return;
            _list.SelectRow(row);
            _selected = index;
            _form.Close(true);
        }

        private void Filter()
        {
            var needle = _search?.GetText().Trim() ?? string.Empty;
            Gtk.ListBoxRow? heading = null;
            var headingVisible = false;
            foreach (var (row, widget, _) in _rows)
            {
                if (row.IsHeading)
                {
                    heading?.SetVisible(headingVisible);
                    heading = widget;
                    headingVisible = false;
                    continue;
                }
                var visible = needle.Length == 0
                    || row.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                    || (row.Subtitle?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false);
                widget.SetVisible(visible);
                headingVisible |= visible;
            }
            heading?.SetVisible(headingVisible);

            if (_selected >= 0 && !_rows[_selected].Widget.GetVisible())
            {
                _list.UnselectAll();
                _selected = -1;
            }
            _form.Refresh();
        }

#if DEBUG
        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"title={_request.Title} search={_search is not null} footer={_request.FooterLinkText} confirm={_form.Dialog.GetTitle()}");
            foreach (var (row, widget, index) in _rows)
                text.AppendLine($"{index}|{(row.IsHeading ? "heading" : "row")}|{row.Title}|{row.Subtitle}|{row.IsEnabled}|{widget.GetVisible()}|{index == _selected}|{row.DisabledReason}");
            return text.ToString();
        }

        public bool Choose(int index)
        {
            if (index < 0 || index >= _rows.Count || !Selectable(_rows[index].Row)) return false;
            _list.SelectRow(_rows[index].Widget);
            _selected = index;
            _form.DebugConfirm();
            return true;
        }

        public void Search(string text) => _search?.SetText(text);
#endif
    }
}
