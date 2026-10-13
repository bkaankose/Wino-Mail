using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// The search filter editor popover (UIKit port of the Mac <c>MailListSearchFilterPopover</c>, Windows SearchFilterFlyout):
/// search scope, From, Subject, Keywords, Date with a custom range, Read status and the attachment and flag filters, over
/// the shared <see cref="MailSearchFilterEditor"/>. Controls write into the editor as they change; nothing applies until
/// Search, so Cancel and tapping outside leave the current search unchanged.
/// </summary>
public sealed class MailListFilterPopoverViewController : UIViewController
{
    public static readonly CGSize PreferredSize = new(420, 560);

    private readonly MailSearchFilterEditor _editor;
    private readonly Action _search;
    private readonly UIButton _scope;
    private readonly UITextField _sender;
    private readonly UITextField _subject;
    private readonly UITextField _keywords;
    private readonly UILabel _keywordsHint;
    private readonly UIButton _dateRange;
    private readonly UIDatePicker _startDate;
    private readonly UIDatePicker _endDate;
    private readonly UIStackView _customRange;
    private readonly UIButton _readStatus;
    private readonly UISwitch _attachments = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISwitch _flagged = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private bool _updating;
    private bool _searched;

    /// <param name="search">Runs when the user taps Search; the editor holds the edited values.</param>
    public MailListFilterPopoverViewController(MailSearchFilterEditor editor, Action search)
    {
        _editor = editor;
        _search = search;
        _scope = PopUp(Translator.SearchBar_SearchIn);
        _sender = Field(Translator.SearchBar_From, Translator.SearchBar_SenderPlaceholder, value => _editor.Sender = value);
        _subject = Field(Translator.SearchBar_Subject, Translator.SearchBar_SubjectPlaceholder, value => _editor.Subject = value);
        _keywords = Field(Translator.SearchBar_Keywords, Translator.SearchBar_KeywordsPlaceholder, value => _editor.Keywords = value);
        _keywordsHint = WinoStyle.Label(Translator.SearchBar_KeywordsLocalHint, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        _dateRange = PopUp(Translator.SearchBar_Date);
        _startDate = DatePicker(Translator.SearchBar_DateStart, date => _editor.CustomStartDate = date);
        _endDate = DatePicker(Translator.SearchBar_DateEnd, date => _editor.CustomEndDate = date);
        _customRange = WinoLayout.HStack(8, _startDate, _endDate);
        _readStatus = PopUp(Translator.SearchBar_ReadStatus);
        _attachments.ValueChanged += (_, _) => { if (!_updating) _editor.HasAttachments = _attachments.On; };
        _flagged.ValueChanged += (_, _) => { if (!_updating) _editor.IsFlagged = _flagged.On; };
    }

    /// <summary>Raised once the popover is gone (Search, Cancel or a tap outside).</summary>
    public event EventHandler? Closed;

    public override void LoadView()
    {
        var title = WinoStyle.Label(Translator.SearchBar_Filters, WinoStyle.BodyStrong);
        var form = WinoLayout.VStack(10,
            Row(Translator.SearchBar_SearchIn, _scope),
            Row(Translator.SearchBar_From, _sender),
            Row(Translator.SearchBar_Subject, _subject),
            Row(Translator.SearchBar_Keywords, WinoLayout.VStack(4, _keywords, _keywordsHint)),
            Row(Translator.SearchBar_Date, _dateRange),
            _customRange,
            Row(Translator.SearchBar_ReadStatus, _readStatus),
            Row(Translator.SearchBar_HasAttachments, _attachments),
            Row(Translator.SearchBar_Flagged, _flagged));
        form.Alignment = UIStackViewAlignment.Fill;

        var reset = Button(Translator.SearchBar_ResetFilters, UIButtonConfiguration.PlainButtonConfiguration, () =>
        {
            _editor.ResetCommand.Execute(null);
            Load();
        });
        var cancel = Button(Translator.Buttons_Cancel, UIButtonConfiguration.GrayButtonConfiguration, () => PadShellHost.DismissPopover(this));
        var search = Button(Translator.SearchBar_SearchButton, UIButtonConfiguration.FilledButtonConfiguration, Search);
        var footer = WinoLayout.HStack(8, reset, WinoLayout.Spacer(), cancel, search);

        var stack = WinoLayout.Insets(WinoLayout.VStack(12, title, form, new WinoSeparator(), footer), 16, 16, 16, 16);
        stack.Alignment = UIStackViewAlignment.Fill;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = false };
        WinoLayout.Fill(stack, scroll);
        stack.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor).Active = true;
        var root = new UIView { BackgroundColor = UIColor.SystemBackground };
        WinoLayout.Fill(scroll, root);
        WinoAccessibility.Label(stack, Translator.SearchBar_Filters);
        View = root;
        Load();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _sender.BecomeFirstResponder();
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        if (IsBeingDismissed || PresentingViewController is null) Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Search()
    {
        if (_searched) return;
        _searched = true;
        // A reversed custom range is swapped, as on the Mac.
        if (_editor.IsCustomDateRange && _editor.CustomStartDate is { } start && _editor.CustomEndDate is { } end && start > end)
        {
            _editor.CustomStartDate = end;
            _editor.CustomEndDate = start;
        }
        PadShellHost.DismissPopover(this);
        _search();
    }

    /// <summary>Reads every control from the editor (after Load, Reset or a date range change).</summary>
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
            // Choosing Custom seeds a 30-day range in the editor; Load shows it.
            SetMenu(_dateRange, _editor.DateRangeOptions, _editor.SelectedDateRange, option => { _editor.SelectedDateRange = option; Load(); });
            _customRange.Hidden = !_editor.IsCustomDateRange;
            var today = DateTime.Today;
            _startDate.Date = ToNSDate(_editor.CustomStartDate?.Date ?? today.AddDays(-29));
            _endDate.Date = ToNSDate(_editor.CustomEndDate?.Date ?? today);
            SetMenu(_readStatus, _editor.ReadStatusOptions, _editor.SelectedReadStatus, option => _editor.SelectedReadStatus = option);
            _attachments.On = _editor.HasAttachments;
            _flagged.On = _editor.IsFlagged;
        }
        finally { _updating = false; }
    }

    private void SetMenu(UIButton button, IReadOnlyList<MailSearchFilterOption> options, MailSearchFilterOption? selected, Action<MailSearchFilterOption> changed)
    {
        var actions = new UIMenuElement[options.Count];
        for (int index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var action = UIAction.Create(option.Title, null, null, _ =>
            {
                if (_updating) return;
                changed(option);
                Load();
            });
            action.State = selected is not null && option.Value == selected.Value ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }
        button.Menu = UIMenu.Create(actions);
        if (button.Configuration is { } configuration)
        {
            configuration.Title = selected?.Title ?? (options.Count > 0 ? options[0].Title : string.Empty);
            button.Configuration = configuration;
        }
    }

    private static UIButton PopUp(string label)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        WinoAccessibility.Label(button, label);
        return button;
    }

    private UITextField Field(string label, string placeholder, Action<string> changed)
    {
        var field = new UITextField
        {
            Placeholder = placeholder,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ReturnKeyType = UIReturnKeyType.Search,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.EditingChanged += (_, _) => { if (!_updating) changed(field.Text ?? string.Empty); };
        // Return in a field searches, as it does in the search box.
        field.ShouldReturn = _ => { Search(); return false; };
        WinoAccessibility.Label(field, label);
        return field;
    }

    private UIDatePicker DatePicker(string label, Action<DateTimeOffset?> changed)
    {
        var picker = new UIDatePicker
        {
            Mode = UIDatePickerMode.Date,
            PreferredDatePickerStyle = UIDatePickerStyle.Compact,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        picker.ValueChanged += (_, _) => { if (!_updating) changed(new DateTimeOffset(ToLocalDate(picker.Date))); };
        WinoAccessibility.Label(picker, label);
        return picker;
    }

    private static UIStackView Row(string text, UIView control)
    {
        var label = WinoStyle.Label(text, WinoStyle.Subheadline, WinoStyle.SecondaryText);
        WinoLayout.Size(label, 110);
        var row = WinoLayout.HStack(12, label, control);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    private static UIButton Button(string title, UIButtonConfiguration configuration, Action action)
    {
        configuration.Title = title;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    /// <summary>The picker works in local time; only the calendar date is kept.</summary>
    private static DateTime ToLocalDate(NSDate date) => ((DateTime)date).ToLocalTime().Date;

    private static NSDate ToNSDate(DateTime localDate) => (NSDate)DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Closed = null;
        base.Dispose(disposing);
    }
}
