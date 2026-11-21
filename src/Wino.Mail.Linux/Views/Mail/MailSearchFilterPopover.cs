using System.Globalization;
using System.Text;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The advanced search filters (macOS <c>MailListSearchFilterPopover</c>, Windows <c>SearchFilterFlyout</c>): every control
/// writes into <see cref="MailSearchFilterEditor"/> as it changes; Cancel just closes, since the next open loads the editor
/// from the applied search again. Search closes first and applies one main-loop turn later.
/// </summary>
internal sealed class MailSearchFilterPopover : IDisposable
{
    private readonly MailSearchFilterEditor _editor;
    private readonly Action _search;
    private readonly Gtk.Popover _popover;
    private readonly Gtk.DropDown _scope;
    private readonly Gtk.Entry _from;
    private readonly Gtk.Entry _subject;
    private readonly Gtk.Entry _keywords;
    private readonly Gtk.Label _hint;
    private readonly Gtk.DropDown _date;
    private readonly Gtk.Box _customRow;
    private readonly Gtk.Button _start;
    private readonly Gtk.Button _end;
    private readonly Gtk.DropDown _read;
    private readonly Gtk.CheckButton _attachments;
    private readonly Gtk.CheckButton _flagged;
    private readonly List<IDisposable> _subscriptions = [];
    private Gtk.Popover? _calendarPopover;
    private IDisposable? _calendarSubscription;
    private bool _updating;
    private bool _shown;
    private bool _disposed;

    public MailSearchFilterPopover(MailSearchFilterEditor editor, Action search)
    {
        WinoLiveObjects.Track(this);
        _editor = editor;
        _search = search;

        _scope = Gtk.DropDown.NewFromStrings(Titles(editor.ScopeOptions));
        _from = Entry(Translator.SearchBar_SenderPlaceholder, Translator.SearchBar_From);
        _subject = Entry(Translator.SearchBar_SubjectPlaceholder, Translator.SearchBar_Subject);
        _keywords = Entry(Translator.SearchBar_KeywordsPlaceholder, Translator.SearchBar_Keywords);
        _hint = Gtk.Label.New(Translator.SearchBar_KeywordsLocalHint);
        _hint.AddCssClass("caption");
        _hint.AddCssClass("dim-label");
        _hint.SetXalign(0);
        _hint.SetWrap(true);
        _hint.SetMaxWidthChars(36);
        var keywords = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        keywords.Append(_keywords);
        keywords.Append(_hint);
        _date = Gtk.DropDown.NewFromStrings(Titles(editor.DateRangeOptions));
        _start = Gtk.Button.NewWithLabel(string.Empty);
        _end = Gtk.Button.NewWithLabel(string.Empty);
        _customRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _customRow.Append(Caption(Translator.SearchBar_DateStart));
        _customRow.Append(_start);
        _customRow.Append(Caption(Translator.SearchBar_DateEnd));
        _customRow.Append(_end);
        _read = Gtk.DropDown.NewFromStrings(Titles(editor.ReadStatusOptions));
        _attachments = Gtk.CheckButton.NewWithLabel(Translator.SearchBar_HasAttachments);
        _flagged = Gtk.CheckButton.NewWithLabel(Translator.SearchBar_Flagged);
        var onlyShow = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        onlyShow.Append(_attachments);
        onlyShow.Append(_flagged);
        WinoA11y.Label(_scope, Translator.SearchBar_SearchIn);
        WinoA11y.Label(_date, Translator.SearchBar_Date);
        WinoA11y.Label(_read, Translator.SearchBar_ReadStatus);

        var grid = Gtk.Grid.New();
        grid.SetRowSpacing(8);
        grid.SetColumnSpacing(12);
        AddRow(grid, 0, Translator.SearchBar_SearchIn, _scope);
        AddRow(grid, 1, Translator.SearchBar_From, _from);
        AddRow(grid, 2, Translator.SearchBar_Subject, _subject);
        AddRow(grid, 3, Translator.SearchBar_Keywords, keywords, top: true);
        AddRow(grid, 4, Translator.SearchBar_Date, _date);
        AddRow(grid, 5, null, _customRow);
        AddRow(grid, 6, Translator.SearchBar_ReadStatus, _read);
        AddRow(grid, 7, Translator.SearchBar_OnlyShow, onlyShow);

        var title = Gtk.Label.New(Translator.SearchBar_Filters);
        title.AddCssClass("heading");
        title.SetXalign(0);

        var reset = Gtk.Button.NewWithLabel(Translator.SearchBar_ResetFilters);
        var cancel = Gtk.Button.NewWithLabel(Translator.Buttons_Cancel);
        var searchButton = Gtk.Button.NewWithLabel(Translator.SearchBar_SearchButton);
        searchButton.AddCssClass("suggested-action");
        searchButton.SetSizeRequest(96, -1);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        footer.Append(reset);
        footer.Append(spacer);
        footer.Append(cancel);
        footer.Append(searchButton);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        content.SetMarginTop(16);
        content.SetMarginBottom(16);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        content.Append(title);
        content.Append(grid);
        content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        content.Append(footer);

        _popover = Gtk.Popover.New();
        _popover.SetAutohide(true);
        _popover.SetPosition(Gtk.PositionType.Bottom);
        _popover.SetChild(content);
        _popover.SetDefaultWidget(searchButton);
        WinoA11y.Label(_popover, Translator.SearchBar_Filters);

        _subscriptions.Add(GtkSignals.OnNotify(_scope, "selected", () => Pick(_scope, editor.ScopeOptions, option => editor.SelectedScope = option)));
        _subscriptions.Add(GtkSignals.OnNotify(_date, "selected", () =>
        {
            if (Pick(_date, editor.DateRangeOptions, option => editor.SelectedDateRange = option)) Load();
        }));
        _subscriptions.Add(GtkSignals.OnNotify(_read, "selected", () => Pick(_read, editor.ReadStatusOptions, option => editor.SelectedReadStatus = option)));
        _subscriptions.Add(GtkSignals.OnChanged(_from, () => { if (!_updating) editor.Sender = _from.GetText(); }));
        _subscriptions.Add(GtkSignals.OnChanged(_subject, () => { if (!_updating) editor.Subject = _subject.GetText(); }));
        _subscriptions.Add(GtkSignals.OnChanged(_keywords, () => { if (!_updating) editor.Keywords = _keywords.GetText(); }));
        _subscriptions.Add(GtkSignals.OnToggled(_attachments, () => { if (!_updating) editor.HasAttachments = _attachments.GetActive(); }));
        _subscriptions.Add(GtkSignals.OnToggled(_flagged, () => { if (!_updating) editor.IsFlagged = _flagged.GetActive(); }));
        _subscriptions.Add(GtkSignals.OnClicked(_start, () => ShowCalendar(_start, true)));
        _subscriptions.Add(GtkSignals.OnClicked(_end, () => ShowCalendar(_end, false)));
        _subscriptions.Add(GtkSignals.OnClicked(reset, Reset));
        _subscriptions.Add(GtkSignals.OnClicked(cancel, Close));
        _subscriptions.Add(GtkSignals.OnClicked(searchButton, Search));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Popover>(h => _popover.OnClosed += h, h => _popover.OnClosed -= h, (_, _) =>
        {
            _shown = false;
            Closed?.Invoke();
        }));

        Load();
    }

    /// <summary>Search, Cancel, Escape or a click outside.</summary>
    public event Action? Closed;

    public bool IsShown => _shown && !_disposed;

    public Gtk.Widget Widget => _popover;

    public void Show(Gtk.Widget anchor)
    {
        if (_disposed) return;
        _popover.SetParent(anchor);
        _popover.Popup();
        _shown = true;
        var from = _from;
        MainLoop.Post(() =>
        {
            if (!_disposed) from.GrabFocus();
        });
    }

    public void Close()
    {
        if (!_disposed) _popover.Popdown();
    }

    public void Search()
    {
        if (_disposed) return;
        if (_editor.IsCustomDateRange && _editor.CustomStartDate is { } start && _editor.CustomEndDate is { } end && start > end)
        {
            _editor.CustomStartDate = end;
            _editor.CustomEndDate = start;
        }
        // The owner disposes the popover on the turn after it closed, before this posted search runs: the search must
        // not depend on the popover still being alive.
        var search = _search;
        Close();
        MainLoop.Post(search);
    }

    public void Reset()
    {
        if (_disposed) return;
        _editor.ResetCommand.Execute(null);
        Load();
    }

    /// <summary>Writes a control (not the editor) so the control's own handler runs: scope|from|subject|keywords|date|start|end|read|attachments|flagged.</summary>
    internal bool Set(string field, string value)
    {
        switch (field)
        {
            case "scope": _scope.SetSelected(uint.Parse(value, CultureInfo.InvariantCulture)); return true;
            case "date": _date.SetSelected(uint.Parse(value, CultureInfo.InvariantCulture)); return true;
            case "read": _read.SetSelected(uint.Parse(value, CultureInfo.InvariantCulture)); return true;
            case "from": _from.SetText(value); return true;
            case "subject": _subject.SetText(value); return true;
            case "keywords": _keywords.SetText(value); return true;
            case "attachments": _attachments.SetActive(value == "true"); return true;
            case "flagged": _flagged.SetActive(value == "true"); return true;
            case "start":
            case "end":
                SetDate(field == "start", DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;
            default: return false;
        }
    }

    /// <summary><c>shown|scope|from|subject|keywords|hint|date|custom|start|end|read|attachments|flagged</c></summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(IsShown).Append('|').Append(_editor.SelectedScope?.Title).Append('|').Append(_from.GetText()).Append('|').Append(_subject.GetText())
            .Append('|').Append(_keywords.GetText()).Append('|').Append(_hint.GetVisible()).Append('|').Append(_editor.SelectedDateRange?.Title)
            .Append('|').Append(_customRow.GetVisible()).Append('|').Append(_start.GetLabel()).Append('|').Append(_end.GetLabel())
            .Append('|').Append(_editor.SelectedReadStatus?.Title).Append('|').Append(_attachments.GetActive()).Append('|').Append(_flagged.GetActive());
        return text.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseCalendar();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Closed = null;
        _popover.Popdown();
        if (_popover.GetParent() is not null) _popover.Unparent();
    }

    private void Load()
    {
        _updating = true;
        try
        {
            Select(_scope, _editor.ScopeOptions, _editor.SelectedScope);
            SetText(_from, _editor.Sender);
            SetText(_subject, _editor.Subject);
            SetText(_keywords, _editor.Keywords);
            _hint.SetVisible(_editor.IsLocalSearch);
            Select(_date, _editor.DateRangeOptions, _editor.SelectedDateRange);
            _customRow.SetVisible(_editor.IsCustomDateRange);
            var today = DateTime.Today;
            ShowDate(_start, Translator.SearchBar_DateStart, _editor.CustomStartDate?.Date ?? today.AddDays(-29));
            ShowDate(_end, Translator.SearchBar_DateEnd, _editor.CustomEndDate?.Date ?? today);
            Select(_read, _editor.ReadStatusOptions, _editor.SelectedReadStatus);
            _attachments.SetActive(_editor.HasAttachments);
            _flagged.SetActive(_editor.IsFlagged);
        }
        finally { _updating = false; }
    }

    private bool Pick(Gtk.DropDown dropDown, MailSearchFilterOption[] options, Action<MailSearchFilterOption> assign)
    {
        if (_updating) return false;
        var index = dropDown.GetSelected();
        if (index >= options.Length) return false;
        assign(options[index]);
        return true;
    }

    private void SetDate(bool isStart, DateTime date)
    {
        var value = new DateTimeOffset(new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local));
        if (isStart) _editor.CustomStartDate = value;
        else _editor.CustomEndDate = value;
        if (isStart) ShowDate(_start, Translator.SearchBar_DateStart, value.Date);
        else ShowDate(_end, Translator.SearchBar_DateEnd, value.Date);
    }

    private void ShowCalendar(Gtk.Button button, bool isStart)
    {
        CloseCalendar();
        var current = (isStart ? _editor.CustomStartDate : _editor.CustomEndDate)?.Date ?? DateTime.Today;
        var calendar = Gtk.Calendar.New();
        // Day 1 first: moving 31 January to February must never pass through an invalid date. The month is 0-based.
        calendar.SetDay(1);
        calendar.SetYear(current.Year);
        calendar.SetMonth(current.Month - 1);
        calendar.SetDay(current.Day);
        var popover = Gtk.Popover.New();
        popover.SetChild(calendar);
        popover.SetParent(button);
        _calendarPopover = popover;
        _calendarSubscription = GtkSignals.Connect<Gtk.Calendar>(h => calendar.OnDaySelected += h, h => calendar.OnDaySelected -= h, (sender, _) =>
        {
            var date = sender.GetDate();
            SetDate(isStart, new DateTime(date.GetYear(), date.GetMonth(), date.GetDayOfMonth()));
            popover.Popdown();
            MainLoop.Post(() =>
            {
                if (ReferenceEquals(_calendarPopover, popover)) CloseCalendar();
            });
        });
        popover.Popup();
    }

    private void CloseCalendar()
    {
        _calendarSubscription?.Dispose();
        _calendarSubscription = null;
        if (_calendarPopover is not { } popover) return;
        _calendarPopover = null;
        popover.Popdown();
        if (popover.GetParent() is not null) popover.Unparent();
    }

    private static void ShowDate(Gtk.Button button, string caption, DateTime date)
    {
        var text = date.ToString("d", CultureInfo.CurrentCulture);
        button.SetLabel(text);
        WinoA11y.Rename(button, $"{caption}: {text}");
    }

    private static void Select(Gtk.DropDown dropDown, MailSearchFilterOption[] options, MailSearchFilterOption? selected)
    {
        var index = selected is null ? 0 : Array.FindIndex(options, option => option.Value == selected.Value);
        dropDown.SetSelected((uint)Math.Max(0, index));
    }

    private static void SetText(Gtk.Entry entry, string? text)
    {
        if (!string.Equals(entry.GetText(), text ?? string.Empty, StringComparison.Ordinal)) entry.SetText(text ?? string.Empty);
    }

    private static string[] Titles(MailSearchFilterOption[] options) => [.. options.Select(static option => option.Title)];

    private static Gtk.Entry Entry(string placeholder, string name)
    {
        var entry = Gtk.Entry.New();
        entry.SetPlaceholderText(placeholder);
        entry.SetActivatesDefault(true);
        entry.SetSizeRequest(260, -1);
        WinoA11y.Label(entry, name);
        return entry;
    }

    private static Gtk.Label Caption(string text)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("dim-label");
        return label;
    }

    private static void AddRow(Gtk.Grid grid, int row, string? caption, Gtk.Widget control, bool top = false)
    {
        if (caption is not null)
        {
            var label = Caption(caption);
            label.SetXalign(1);
            label.SetValign(top ? Gtk.Align.Start : Gtk.Align.Center);
            if (top) label.SetMarginTop(6);
            grid.Attach(label, 0, row, 1, 1);
        }
        control.SetHexpand(true);
        grid.Attach(control, 1, row, 1, 1);
    }
}
