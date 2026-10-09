using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The search filter editor (Windows MailListPage SearchFilterFlyout): search scope, From, Subject,
/// Keywords, Date with a custom range, Read status and the attachment and flag filters, over the
/// shared <see cref="MailSearchFilterEditor"/>. Controls write into the editor as they change; nothing
/// applies until Search, so Cancel, Esc and clicking outside leave the current search unchanged.
/// </summary>
internal sealed class MailListSearchFilterPopover : NSObject
{
    private const double LabelWidth = 100;
    private const double FieldWidth = 300;

    private readonly MailSearchFilterEditor _editor;
    private readonly Action _search;
    private readonly NSPopover _popover;
    private readonly NSPopUpButton _scope;
    private readonly NSTextField _sender;
    private readonly NSTextField _subject;
    private readonly NSTextField _keywords;
    private readonly NSTextField _keywordsHint;
    private readonly NSPopUpButton _dateRange;
    private readonly NSDatePicker _startDate;
    private readonly NSDatePicker _endDate;
    private readonly NSStackView _customRange;
    private readonly NSPopUpButton _readStatus;
    private readonly NSButton _attachments;
    private readonly NSButton _flagged;
    private bool _updating;
    private bool _disposed;
    private NSObject? _closeObserver;

    /// <param name="search">Runs when the user presses Search; the editor holds the edited values.</param>
    public MailListSearchFilterPopover(MailSearchFilterEditor editor, Action search)
    {
        _editor = editor;
        _search = search;

        _scope = PopUp(editor.ScopeOptions, Translator.SearchBar_SearchIn, () => _editor.SelectedScope = Selected(_scope, _editor.ScopeOptions));
        _sender = Field(Translator.SearchBar_From, Translator.SearchBar_SenderPlaceholder, value => _editor.Sender = value);
        _subject = Field(Translator.SearchBar_Subject, Translator.SearchBar_SubjectPlaceholder, value => _editor.Subject = value);
        _keywords = Field(Translator.SearchBar_Keywords, Translator.SearchBar_KeywordsPlaceholder, value => _editor.Keywords = value);
        _keywordsHint = WinoStyle.Label(Translator.SearchBar_KeywordsLocalHint, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        _keywordsHint.PreferredMaxLayoutWidth = (nfloat)FieldWidth;
        var keywords = WinoLayout.VStack(4, _keywords, _keywordsHint);

        _dateRange = PopUp(editor.DateRangeOptions, Translator.SearchBar_Date, () =>
        {
            _editor.SelectedDateRange = Selected(_dateRange, _editor.DateRangeOptions);
            // Choosing Custom seeds a 30-day range in the editor; show it.
            Load();
        });
        _startDate = DatePicker(Translator.SearchBar_DateStart, date => _editor.CustomStartDate = date);
        _endDate = DatePicker(Translator.SearchBar_DateEnd, date => _editor.CustomEndDate = date);
        _customRange = WinoLayout.HStack(8, _startDate, _endDate);

        _readStatus = PopUp(editor.ReadStatusOptions, Translator.SearchBar_ReadStatus, () => _editor.SelectedReadStatus = Selected(_readStatus, _editor.ReadStatusOptions));
        _attachments = NSButton.CreateCheckbox(Translator.SearchBar_HasAttachments, () => { if (!_updating) _editor.HasAttachments = _attachments!.State == NSCellStateValue.On; });
        _flagged = NSButton.CreateCheckbox(Translator.SearchBar_Flagged, () => { if (!_updating) _editor.IsFlagged = _flagged!.State == NSCellStateValue.On; });
        var onlyShow = WinoLayout.HStack(16, _attachments, _flagged);

        var grid = NSGridView.Create(new NSView[][]
        {
            [RowLabel(Translator.SearchBar_SearchIn), _scope],
            [RowLabel(Translator.SearchBar_From), _sender],
            [RowLabel(Translator.SearchBar_Subject), _subject],
            [RowLabel(Translator.SearchBar_Keywords), keywords],
            [RowLabel(Translator.SearchBar_Date), _dateRange],
            [NSGridCell.EmptyContentView, _customRange],
            [RowLabel(Translator.SearchBar_ReadStatus), _readStatus],
            [RowLabel(Translator.SearchBar_OnlyShow), onlyShow]
        });
        grid.ColumnSpacing = 12;
        grid.RowSpacing = 8;
        grid.RowAlignment = NSGridRowAlignment.FirstBaseline;
        grid.GetColumn(0).X = NSGridCellPlacement.Trailing;
        grid.GetColumn(0).Width = (nfloat)LabelWidth;
        grid.GetColumn(1).Width = (nfloat)FieldWidth;
        // The keywords cell is a field over a hint: its label aligns with the field's top line.
        grid.GetRow(3).RowAlignment = NSGridRowAlignment.None;
        grid.GetRow(3).Y = NSGridCellPlacement.Top;
        grid.TranslatesAutoresizingMaskIntoConstraints = false;

        var reset = new NSButton { Title = Translator.SearchBar_ResetFilters, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        reset.Activated += (_, _) =>
        {
            _editor.ResetCommand.Execute(null);
            Load();
        };
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        cancel.Activated += (_, _) => Close();
        var searchButton = new NSButton { Title = Translator.SearchBar_SearchButton, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", TranslatesAutoresizingMaskIntoConstraints = false };
        searchButton.Activated += (_, _) => Search();
        WinoLayout.Size(searchButton, 96);
        var footer = WinoLayout.HStack(8, reset, WinoLayout.Spacer(), cancel, searchButton);

        var title = WinoStyle.Label(Translator.SearchBar_Filters, WinoStyle.BodyStrong);
        var separator = new WinoSeparator();
        var stack = WinoLayout.VStack(10, title, grid, separator, footer);
        stack.EdgeInsets = new NSEdgeInsets(14, 16, 14, 16);
        foreach (var view in new NSView[] { separator, footer }) view.WidthAnchor.ConstraintEqualTo(grid.WidthAnchor).Active = true;
        WinoAccessibility.Label(stack, Translator.SearchBar_Filters);

        var controller = new NSViewController { View = stack };
        _popover = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentViewController = controller
        };
        _closeObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSPopover.DidCloseNotification, _ => Closed?.Invoke(this, EventArgs.Empty), _popover);
        Load();
    }

    /// <summary>The popover closed (Search, Cancel, Esc or a click outside). Raised inside AppKit's close callback.</summary>
    public event EventHandler? Closed;

    /// <summary>Shows the popover under <paramref name="anchor"/> and focuses the From field.</summary>
    public void Show(NSView anchor)
    {
        _popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
        _popover.ContentViewController.View.Window?.MakeFirstResponder(_sender);
    }

    public bool IsShown => _popover.Shown;

    public void Close()
    {
        if (!_disposed) _popover.Close();
    }

    private void Search()
    {
        // Text fields commit on every change; a date typed but not yet committed is read here. A reversed range is swapped.
        if (_editor.IsCustomDateRange)
        {
            _editor.CustomStartDate = new DateTimeOffset(ToLocalDate(_startDate.DateValue));
            _editor.CustomEndDate = new DateTimeOffset(ToLocalDate(_endDate.DateValue));
        }
        if (_editor.IsCustomDateRange && _editor.CustomStartDate is { } start && _editor.CustomEndDate is { } end && start > end)
        {
            _editor.CustomStartDate = end;
            _editor.CustomEndDate = start;
        }
        // Deferred: Return arrives inside the field editor's insertNewline: command, which should
        // finish before the popover (and its field editor) is torn down.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_disposed) return;
            Close();
            _search();
        });
    }

    /// <summary>Reads every control from the editor (after Load, Reset or a date range change).</summary>
    private void Load()
    {
        _updating = true;
        try
        {
            Select(_scope, _editor.ScopeOptions, _editor.SelectedScope);
            _sender.StringValue = _editor.Sender ?? string.Empty;
            _subject.StringValue = _editor.Subject ?? string.Empty;
            _keywords.StringValue = _editor.Keywords ?? string.Empty;
            _keywordsHint.Hidden = !_editor.IsLocalSearch;
            Select(_dateRange, _editor.DateRangeOptions, _editor.SelectedDateRange);
            _customRange.Hidden = !_editor.IsCustomDateRange;
            var today = DateTime.Today;
            _startDate.DateValue = ToNSDate(_editor.CustomStartDate?.Date ?? today.AddDays(-29));
            _endDate.DateValue = ToNSDate(_editor.CustomEndDate?.Date ?? today);
            Select(_readStatus, _editor.ReadStatusOptions, _editor.SelectedReadStatus);
            _attachments.State = _editor.HasAttachments ? NSCellStateValue.On : NSCellStateValue.Off;
            _flagged.State = _editor.IsFlagged ? NSCellStateValue.On : NSCellStateValue.Off;
        }
        finally { _updating = false; }
    }

    private NSPopUpButton PopUp(IReadOnlyList<MailSearchFilterOption> options, string label, Action changed)
    {
        var popUp = new NSPopUpButton { PullsDown = false, TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var option in options) popUp.AddItem(option.Title);
        popUp.Activated += (_, _) => { if (!_updating) changed(); };
        WinoAccessibility.Label(popUp, label);
        WinoLayout.Size(popUp, FieldWidth);
        return popUp;
    }

    private NSTextField Field(string label, string placeholder, Action<string> changed)
    {
        var field = new SearchField { PlaceholderString = placeholder, TranslatesAutoresizingMaskIntoConstraints = false };
        field.TextChanged += (_, _) => { if (!_updating) changed(field.StringValue ?? string.Empty); };
        // Return in a field searches, as it does in the search box (the field editor sends insertNewline:).
        field.ReturnPressed = Search;
        WinoAccessibility.Label(field, label);
        WinoLayout.Size(field, FieldWidth);
        return field;
    }

    private NSDatePicker DatePicker(string label, Action<DateTimeOffset?> changed)
    {
        var picker = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.TextFieldAndStepper,
            DatePickerElements = NSDatePickerElementFlags.YearMonthDate,
            DatePickerMode = NSDatePickerMode.Single,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        picker.Activated += (_, _) => { if (!_updating) changed(new DateTimeOffset(ToLocalDate(picker.DateValue))); };
        WinoAccessibility.Label(picker, label);
        return picker;
    }

    private static NSTextField RowLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body);
        label.Alignment = NSTextAlignment.Right;
        return label;
    }

    private static MailSearchFilterOption Selected(NSPopUpButton popUp, IReadOnlyList<MailSearchFilterOption> options)
    {
        var index = (int)popUp.IndexOfSelectedItem;
        return index >= 0 && index < options.Count ? options[index] : options[0];
    }

    private static void Select(NSPopUpButton popUp, IReadOnlyList<MailSearchFilterOption> options, MailSearchFilterOption? selected)
    {
        int index = selected is null ? 0 : Math.Max(0, IndexOf(options, selected));
        popUp.SelectItem(index);
    }

    private static int IndexOf(IReadOnlyList<MailSearchFilterOption> options, MailSearchFilterOption selected)
    {
        for (int index = 0; index < options.Count; index++)
            if (options[index].Value == selected.Value) return index;
        return -1;
    }

    /// <summary>The picker works in local time; only the calendar date is kept.</summary>
    private static DateTime ToLocalDate(NSDate date) => ((DateTime)date).ToLocalTime().Date;

    private static NSDate ToNSDate(DateTime localDate) => (NSDate)DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);

    /// <summary>Debug: the visible control values.</summary>
    internal string Dump()
        => $"shown={IsShown} scope={_scope.TitleOfSelectedItem} from='{_sender.StringValue}' subject='{_subject.StringValue}' keywords='{_keywords.StringValue}' " +
           $"hint={!_keywordsHint.Hidden} date={_dateRange.TitleOfSelectedItem} custom={!_customRange.Hidden} read={_readStatus.TitleOfSelectedItem} " +
           $"attachments={_attachments.State} flagged={_flagged.State}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            Closed = null;
            if (_closeObserver is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_closeObserver);
            _closeObserver = null;
            _popover.Close();
            _popover.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A text field that reports Return, so Enter in any field runs the search.</summary>
    private sealed class SearchField : NSTextField
    {
        public Action? ReturnPressed { get; set; }

        public SearchField()
        {
            Delegate = new ReturnDelegate(this);
        }

        private sealed class ReturnDelegate(SearchField owner) : NSTextFieldDelegate
        {
            public override bool DoCommandBySelector(NSControl control, NSTextView textView, ObjCRuntime.Selector commandSelector)
            {
                if (commandSelector.Name != "insertNewline:") return false;
                owner.ReturnPressed?.Invoke();
                return true;
            }

            public override void Changed(NSNotification notification) => owner.OnTextChanged();
        }

        public event EventHandler? TextChanged;

        private void OnTextChanged() => TextChanged?.Invoke(this, EventArgs.Empty);
    }
}
