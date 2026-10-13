using UIKit;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The search filter editor as a phone sheet (Windows MailListPage SearchFilterFlyout, Mac MailListSearchFilterPopover):
/// search scope, From, Subject, Keywords, Date with a custom range, Read status and the attachment and flag filters over
/// the shared <see cref="MailSearchFilterEditor"/>. Controls write into the editor as they change; nothing applies until
/// Search, so Cancel or a swipe down leaves the current search unchanged.
/// </summary>
public sealed class MailListFilterSheetViewController : UIViewController
{
    private readonly MailSearchFilterEditor _editor;
    private readonly Action _search;
    private readonly Action _cancel;
    private readonly UIButton _scope = PullDown();
    private readonly UITextField _sender = Field(Translator.SearchBar_SenderPlaceholder);
    private readonly UITextField _subject = Field(Translator.SearchBar_SubjectPlaceholder);
    private readonly UITextField _keywords = Field(Translator.SearchBar_KeywordsPlaceholder);
    private readonly UILabel _keywordsHint = WinoStyle.Label(Translator.SearchBar_KeywordsLocalHint, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
    private readonly UIButton _dateRange = PullDown();
    private readonly UIDatePicker _startDate = DatePicker();
    private readonly UIDatePicker _endDate = DatePicker();
    private readonly UIView _customRange = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _readStatus = PullDown();
    private readonly UISwitch _attachments = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISwitch _flagged = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private bool _updating;

    /// <param name="search">Runs on Search; the editor holds the edited values.</param>
    public MailListFilterSheetViewController(MailSearchFilterEditor editor, Action search, Action cancel)
    {
        _editor = editor;
        _search = search;
        _cancel = cancel;
        Title = Translator.SearchBar_Filters;
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        _sender.EditingChanged += (_, _) => { if (!_updating) _editor.Sender = _sender.Text ?? string.Empty; };
        _subject.EditingChanged += (_, _) => { if (!_updating) _editor.Subject = _subject.Text ?? string.Empty; };
        _keywords.EditingChanged += (_, _) => { if (!_updating) _editor.Keywords = _keywords.Text ?? string.Empty; };
        foreach (var field in new[] { _sender, _subject, _keywords })
            field.ShouldReturn = _ => { Search(); return false; };
        _startDate.ValueChanged += (_, _) => { if (!_updating) _editor.CustomStartDate = new DateTimeOffset(ToLocalDate(_startDate.Date)); };
        _endDate.ValueChanged += (_, _) => { if (!_updating) _editor.CustomEndDate = new DateTimeOffset(ToLocalDate(_endDate.Date)); };
        _attachments.ValueChanged += (_, _) => { if (!_updating) _editor.HasAttachments = _attachments.On; };
        _flagged.ValueChanged += (_, _) => { if (!_updating) _editor.IsFlagged = _flagged.On; };
        WinoLayout.Fill(WinoLayout.HStack(8, _startDate, _endDate, WinoLayout.Spacer()), _customRange);

        var reset = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
        reset.SetTitle(Translator.SearchBar_ResetFilters, UIControlState.Normal);
        reset.PrimaryActionTriggered += (_, _) =>
        {
            _editor.ResetCommand.Execute(null);
            Load();
        };

        var content = WinoLayout.VStack(16,
            Group(Row(Translator.SearchBar_SearchIn, _scope)),
            Group(Row(Translator.SearchBar_From, _sender), Row(Translator.SearchBar_Subject, _subject), Stacked(Translator.SearchBar_Keywords, _keywords, _keywordsHint)),
            Group(Row(Translator.SearchBar_Date, _dateRange), _customRange, Row(Translator.SearchBar_ReadStatus, _readStatus)),
            Group(Row(Translator.SearchBar_HasAttachments, _attachments), Row(Translator.SearchBar_Flagged, _flagged)),
            reset);
        content.Alignment = UIStackViewAlignment.Fill;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive, AlwaysBounceVertical = true };
        root.AddSubview(scroll);
        scroll.AddSubview(content);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            content.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 16),
            content.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -24),
            content.LeadingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.LeadingAnchor, 16),
            content.TrailingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.TrailingAnchor, -16)
        ]);

        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => _cancel());
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(Translator.SearchBar_SearchButton, UIBarButtonItemStyle.Done, (_, _) => Search());
        View = root;
        Load();
    }

    private void Search()
    {
        View?.EndEditing(true);
        // A reversed custom range is swapped, as the Mac editor does.
        if (_editor.IsCustomDateRange && _editor.CustomStartDate is { } start && _editor.CustomEndDate is { } end && start > end)
        {
            _editor.CustomStartDate = end;
            _editor.CustomEndDate = start;
        }
        _search();
    }

    /// <summary>Reads every control from the editor (after open, Reset or a date range change).</summary>
    private void Load()
    {
        _updating = true;
        try
        {
            SetMenu(_scope, _editor.ScopeOptions, _editor.SelectedScope, option => _editor.SelectedScope = option);
            _sender.Text = _editor.Sender ?? string.Empty;
            _subject.Text = _editor.Subject ?? string.Empty;
            _keywords.Text = _editor.Keywords ?? string.Empty;
            _keywordsHint.Hidden = !_editor.IsLocalSearch;
            SetMenu(_dateRange, _editor.DateRangeOptions, _editor.SelectedDateRange, option =>
            {
                _editor.SelectedDateRange = option;
                // Choosing Custom seeds a 30-day range in the editor; show it.
                Load();
            });
            _customRange.Hidden = !_editor.IsCustomDateRange;
            var today = DateTime.Today;
            _startDate.SetDate(ToNSDate(_editor.CustomStartDate?.Date ?? today.AddDays(-29)), false);
            _endDate.SetDate(ToNSDate(_editor.CustomEndDate?.Date ?? today), false);
            SetMenu(_readStatus, _editor.ReadStatusOptions, _editor.SelectedReadStatus, option => _editor.SelectedReadStatus = option);
            _attachments.On = _editor.HasAttachments;
            _flagged.On = _editor.IsFlagged;
        }
        finally { _updating = false; }
    }

    private static void SetMenu(UIButton button, IReadOnlyList<MailSearchFilterOption> options, MailSearchFilterOption? selected, Action<MailSearchFilterOption> changed)
    {
        var actions = options.Select(option =>
        {
            var action = UIAction.Create(option.Title, null, null, _ => changed(option));
            action.State = selected?.Value == option.Value ? UIMenuElementState.On : UIMenuElementState.Off;
            return (UIMenuElement)action;
        }).ToArray();
        button.Menu = UIMenu.Create(actions);
        if (button.Configuration is { } configuration)
        {
            configuration.Title = (selected ?? (options.Count > 0 ? options[0] : null))?.Title ?? string.Empty;
            button.Configuration = configuration;
        }
    }

    private static UIButton PullDown()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, ChangesSelectionAsPrimaryAction = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 250, 750);
        return button;
    }

    private static UITextField Field(string placeholder)
    {
        var field = new UITextField
        {
            Placeholder = placeholder,
            Font = WinoStyle.Body,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            ReturnKeyType = UIReturnKeyType.Search,
            AutocorrectionType = UITextAutocorrectionType.No,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Priorities(field, UILayoutConstraintAxis.Horizontal, 1, 250);
        return field;
    }

    private static UIDatePicker DatePicker() => new()
    {
        Mode = UIDatePickerMode.Date,
        PreferredDatePickerStyle = UIDatePickerStyle.Compact,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    /// <summary>A grouped card of rows, iOS Settings style, on the Wino group fill.</summary>
    private static UIView Group(params UIView[] rows)
    {
        var card = new WinoSurfaceView { Fill = WinoStyle.GroupFill, CornerRadius = WinoStyle.GroupRadius };
        var stack = WinoLayout.Insets(WinoLayout.VStack(10, rows), 10, 14, 10, 14);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, card);
        return card;
    }

    private static UIView Row(string label, UIView control)
    {
        var title = WinoStyle.Label(label, WinoStyle.Body);
        WinoLayout.Priorities(title, UILayoutConstraintAxis.Horizontal, 750, 1000);
        // Switches sit at the trailing edge; fields and pull-downs take the remaining width.
        var row = control is UISwitch ? WinoLayout.HStack(12, title, WinoLayout.Spacer(), control) : WinoLayout.HStack(12, title, control);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Size(row, -1, 36);
        WinoAccessibility.Label(control, label);
        return row;
    }

    private static UIView Stacked(string label, UIView control, UILabel hint)
    {
        var stack = WinoLayout.VStack(4, Row(label, control), hint);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>The picker works in local time; only the calendar date is kept.</summary>
    private static DateTime ToLocalDate(Foundation.NSDate date) => ((DateTime)date).ToLocalTime().Date;

    private static Foundation.NSDate ToNSDate(DateTime localDate) => (Foundation.NSDate)DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);
}
