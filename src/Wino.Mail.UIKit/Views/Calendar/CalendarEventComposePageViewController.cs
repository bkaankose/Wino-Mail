using System.ComponentModel;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The event composer (Mac CalendarEventComposePageViewController, Windows CalendarEventComposePage) for iPhone and
/// iPad, as summary cards that open in place (option C of the 3 Nov 2026 add-event designs). The host shows it as the
/// root of a navigation controller: Cancel on the left, Save / Send on the right. One scroll of inset cards on the
/// grouped background: the title with the calendar and location (always open); When (dates on one date-and-time wheel
/// each, all day, time zone, repeat with the custom editor); Attendees with contact suggestions inside the card; Details
/// (reminder, show as, private, online meeting); Attachments; then the rich-text notes (the mail composer's WKWebView
/// editor with its Format / Insert / Options bar above the keyboard). A closed card shows its state in one line; one
/// card is open at a time. Partials: Attendees, Recurrence, Drop and Debug.
/// </summary>
public sealed partial class CalendarEventComposePageViewController(CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    IExternalLauncher launcher, IPreferencesService preferences, IServiceProvider services)
    : WinoViewController<CalendarEventComposePageViewModel>(viewModel, dispatcher, logger)
{
    private const double MaximumFormWidth = 640;
    private const double NotesHeight = 240;

    /// <summary>ViewModel properties that only change because the user edited the event (after loading).</summary>
    private static readonly HashSet<string> EditedProperties = new(StringComparer.Ordinal)
    {
        nameof(CalendarEventComposePageViewModel.Title),
        nameof(CalendarEventComposePageViewModel.Location),
        nameof(CalendarEventComposePageViewModel.IsAllDay),
        nameof(CalendarEventComposePageViewModel.StartDate),
        nameof(CalendarEventComposePageViewModel.StartTime),
        nameof(CalendarEventComposePageViewModel.EndDate),
        nameof(CalendarEventComposePageViewModel.EndTime),
        nameof(CalendarEventComposePageViewModel.SelectedRepeatOption),
        nameof(CalendarEventComposePageViewModel.SelectedRecurrenceInterval),
        nameof(CalendarEventComposePageViewModel.SelectedRecurrenceFrequencyOption),
        nameof(CalendarEventComposePageViewModel.RecurrenceEndDate),
        nameof(CalendarEventComposePageViewModel.SelectedReminderOption),
        nameof(CalendarEventComposePageViewModel.SelectedShowAsOption),
        nameof(CalendarEventComposePageViewModel.SelectedTimeZoneOption),
        nameof(CalendarEventComposePageViewModel.IsPrivate),
        nameof(CalendarEventComposePageViewModel.IsOnlineMeeting),
        nameof(CalendarEventComposePageViewModel.SelectedCalendar),
        nameof(CalendarEventComposePageViewModel.AttendeeCount),
        nameof(CalendarEventComposePageViewModel.AttachmentCount)
    };

    private static readonly NSDateFormatter DayFormatter = CreateDayFormatter();
    private static readonly NSDateFormatter TimeFormatter = new() { DateStyle = NSDateFormatterStyle.None, TimeStyle = NSDateFormatterStyle.Short };

    private readonly IExternalLauncher _launcher = launcher;
    private readonly IPreferencesService _preferences = preferences;
    private readonly ITranslationService? _translations = services.GetService(typeof(ITranslationService)) as ITranslationService;
    private readonly IPictureStorageService? _pictures = services.GetService(typeof(IPictureStorageService)) as IPictureStorageService;
    private readonly IMailDialogService? _dialogs = services.GetService(typeof(IMailDialogService)) as IMailDialogService;

    // ---- Layout ----
    private readonly UIScrollView _formScroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };
    private readonly UIStackView _formStack = WinoLayout.VStack(16);
    private readonly ComposeCard _titleCard = new(null);
    private readonly ComposeCard _whenCard = new(WinoIconGlyph.Clock);
    private readonly ComposeCard _attendeesCard = new(WinoIconGlyph.CalendarAttendees);
    private readonly ComposeCard _detailsCard = new(WinoIconGlyph.Reminder);
    private readonly ComposeCard _attachmentsCard = new(WinoIconGlyph.Attachment);
    private readonly ComposeCard _notesCard = new(WinoIconGlyph.Note);

    // ---- Navigation bar ----
    private UIBarButtonItem? _cancelItem;
    private UIBarButtonItem? _saveItem;
    private CommandBinding? _saveBinding;

    // ---- Title, calendar and location ----
    private readonly UITextField _title = new()
    {
        Font = WinoStyle.Scaled(20, UIFontWeight.Semibold),
        AdjustsFontForContentSizeCategory = true,
        BorderStyle = UITextBorderStyle.None,
        ReturnKeyType = UIReturnKeyType.Next,
        AutocapitalizationType = UITextAutocapitalizationType.Sentences,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIView _calendarBar = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _calendarButton = CalendarButton();
    private readonly UITextField _location = new()
    {
        Font = WinoStyle.Body,
        AdjustsFontForContentSizeCategory = true,
        BorderStyle = UITextBorderStyle.None,
        ReturnKeyType = UIReturnKeyType.Next,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    // ---- Details ----
    private readonly UIButton _showAsButton = MenuButton(Translator.CalendarEventDetails_ShowAs);
    private readonly UIButton _reminderButton = MenuButton(Translator.CalendarEventDetails_Reminder);
    private readonly UISwitch _privateSwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISwitch _onlineSwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _onlineLabel = WinoStyle.Label(string.Empty, WinoStyle.Body, null, 2);
    private UIView? _onlineRow;
    private UIView? _onlineInfoRow;

    // ---- When ----
    private readonly UIDatePicker _startPicker = WheelPicker(Translator.CalendarEventCompose_Starts);
    private readonly UIDatePicker _endPicker = WheelPicker(Translator.CalendarEventCompose_Ends);
    private readonly UIButton _startValue = ValueButton(Translator.CalendarEventCompose_Starts);
    private readonly UIButton _endValue = ValueButton(Translator.CalendarEventCompose_Ends);
    private UIView? _startPickerRow;
    private UIView? _endPickerRow;
    private readonly UISwitch _allDaySwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _duration = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 2);
    private readonly UILabel _rangeError = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 2);
    private UIView? _durationRow;
    private readonly UIButton _timeZoneButton = MenuButton(Translator.CalendarEventCompose_TimeZone);
    private readonly UILabel _localTimeHint = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 3);
    private UIView? _timeZoneRow;
    private UIView? _localTimeHintRow;
    private bool _applying;

    // ---- Repeat ----
    private readonly UIButton _repeatButton = MenuButton(Translator.CalendarEventCompose_Repeat);
    private readonly UILabel _recurrenceSummary = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 3);
    private UIView? _recurrenceSummaryRow;
    private UIView? _customRecurrenceRow;

    // ---- Notes (Windows WinoMailEditor): untouched notes are saved exactly as they arrived ----
    private readonly ComposeEditorWebView _notesWebView = new();
    private readonly UIView _notesHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIActivityIndicatorView _notesSpinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private WebKitHtmlMailEditorSession? _notesEditor;
    private EditorFormatToolbar? _formatToolbar;
    private EditorToolbarButton? _themeButton;
    private string _originalNotesHtml = string.Empty;
    private string? _baselineNotesBody;
    private bool _notesReady;
    private bool _notesEdited;
    private bool _notesFailed;
    private bool _notesDisposed;
    private bool _darkEditor;
    private bool? _appearanceDark;

    // ---- Attendees and attachments ----
    private readonly ComposeRecipientField _invite = new(string.Empty);
    private UIView? _inviteRow;
    private readonly UIStackView _attendeeList = WinoLayout.VStack(0);
    private readonly UILabel _attendeesNote = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
    private UIView? _attendeesNoteRow;
    private readonly UIButton _addAttachment = ActionButton(Translator.CalendarEventCompose_AddAttachment, WinoIconGlyph.Add);
    private readonly UILabel _attachmentsDisabled = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 3);
    private UIView? _attachmentsDisabledRow;
    private readonly UIStackView _attachmentList = WinoLayout.VStack(0);

    private bool _released;
    private bool _edited;
    private bool _keyCommandsInstalled;

    /// <summary>
    /// Raised when the composer wants to close: Cancel, or the event was created (<see cref="CalendarEventComposePageViewModel.LastCreatedResult"/>).
    /// The host dismisses and releases the page; the ViewModel's own Cancel (navigation back) is never run.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>True once the user changed something; the sheet then refuses the swipe-down dismissal.</summary>
    public bool HasEdits => _edited;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };

        BuildTitleCard();
        BuildWhenCard();
        BuildAttendeesCard();
        BuildDetailsCard();
        BuildAttachmentsCard();
        BuildNotesCard();

        foreach (var card in new[] { _whenCard, _attendeesCard, _detailsCard, _attachmentsCard })
        {
            var captured = card;
            card.HeaderTapped += (_, _) => ToggleCard(captured);
        }

        _formStack.Alignment = UIStackViewAlignment.Fill;
        foreach (var card in new[] { _titleCard, _whenCard, _attendeesCard, _detailsCard, _attachmentsCard, _notesCard })
            _formStack.AddArrangedSubview(card);
        _formScroll.AddSubview(_formStack);
        var content = _formScroll.ContentLayoutGuide;
        var frame = _formScroll.FrameLayoutGuide;
        // Full width on iPhone; a readable column in a wide iPad sheet.
        var fullWidth = _formStack.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -32);
        fullWidth.Priority = 999;
        NSLayoutConstraint.ActivateConstraints(
        [
            _formStack.TopAnchor.ConstraintEqualTo(content.TopAnchor, 16),
            _formStack.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -24),
            _formStack.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
            _formStack.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)MaximumFormWidth),
            _formStack.WidthAnchor.ConstraintLessThanOrEqualTo(frame.WidthAnchor, 1, -32),
            fullWidth,
            content.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor)
        ]);

        root.AddSubview(_formScroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            _formScroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            _formScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _formScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            // The keyboard guide includes the formatting bar, so the form ends above it while editing.
            _formScroll.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor)
        ]);
        View = root;

        BuildEditorToolbar();
        BuildNavigationItems();
        SetUpAttachmentDrop();
        WinoTraits.OnAppearanceChanged(root, _ => FollowAppearance());
    }

    // ---- Cards ----

    private void BuildTitleCard()
    {
        _title.Placeholder = Translator.CalendarEventCompose_TitlePlaceholder;
        _title.AccessibilityLabel = Translator.CalendarEventCompose_TitlePlaceholder;
        _title.EditingChanged += (_, _) =>
        {
            // Committed text only: an input method's marked text is still being composed.
            if (_title.MarkedTextRange is not null) return;
            ViewModel.Title = _title.Text ?? string.Empty;
        };
        _title.ShouldReturn = _ => { _location.BecomeFirstResponder(); return false; };
        _calendarBar.Layer.CornerRadius = 2;
        WinoLayout.Size(_calendarBar, 4, 28);
        var titleLine = WinoLayout.HStack(12, _calendarBar, _title);
        titleLine.Alignment = UIStackViewAlignment.Center;
        _titleCard.AddRow(titleLine, rule: false, minimumHeight: 56);

        _calendarButton.Menu = DeferredMenu(BuildCalendarMenu);
        _titleCard.AddRow(_calendarButton, insets: 0);

        _location.Placeholder = Translator.CalendarEventCompose_LocationPlaceholder;
        _location.AccessibilityLabel = Translator.CalendarEventCompose_LocationPlaceholder;
        _location.EditingChanged += (_, _) =>
        {
            if (_location.MarkedTextRange is not null) return;
            ViewModel.Location = _location.Text ?? string.Empty;
        };
        _location.ShouldReturn = _ => { FocusNotes(); return false; };
        _titleCard.AddRow(GlyphLine(WinoIconGlyph.Location, _location));
    }

    private void BuildWhenCard()
    {
        _allDaySwitch.ValueChanged += (_, _) => ViewModel.IsAllDay = _allDaySwitch.On;
        WinoAccessibility.Label(_allDaySwitch, Translator.CalendarEventCompose_AllDay);
        _whenCard.AddRow(SwitchRow(WinoStyle.Label(Translator.CalendarEventCompose_AllDay, WinoStyle.Body), _allDaySwitch));

        foreach (var picker in new[] { _startPicker, _endPicker }) picker.ValueChanged += (_, _) => PushDates();
        _startValue.PrimaryActionTriggered += (_, _) => ToggleWheel(start: true);
        _endValue.PrimaryActionTriggered += (_, _) => ToggleWheel(start: false);
        _whenCard.AddRow(ValueRow(Translator.CalendarEventCompose_Starts, _startValue));
        _startPickerRow = _whenCard.AddRow(_startPicker, rule: false, insets: 0);
        _startPickerRow.Hidden = true;
        _whenCard.AddRow(ValueRow(Translator.CalendarEventCompose_Ends, _endValue));
        _endPickerRow = _whenCard.AddRow(_endPicker, rule: false, insets: 0);
        _endPickerRow.Hidden = true;

        _rangeError.Hidden = true;
        _durationRow = _whenCard.AddRow(WinoLayout.VStack(2, _duration, _rangeError), rule: false, minimumHeight: 0);

        _timeZoneButton.Menu = DeferredMenu(BuildTimeZoneMenu);
        _timeZoneRow = _whenCard.AddRow(ValueRow(Translator.CalendarEventCompose_TimeZone, _timeZoneButton));
        _localTimeHintRow = _whenCard.AddRow(_localTimeHint, rule: false, minimumHeight: 0);
        _localTimeHintRow.Hidden = true;

        _repeatButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.RepeatOptions, ViewModel.SelectedRepeatOption, option => option.DisplayText, option => ViewModel.SelectedRepeatOption = option));
        _whenCard.AddRow(ValueRow(Translator.CalendarEventCompose_Repeat, _repeatButton));
        // The rows, not their content, hide: a hidden view outside a stack still takes its height.
        _customRecurrenceRow = _whenCard.AddRow(BuildCustomRecurrence(), rule: false, minimumHeight: 0);
        _customRecurrenceRow.Hidden = true;
        _customRecurrence.Hidden = false;
        var recurrenceEnd = BuildRecurrenceEnd();
        recurrenceEnd.Hidden = false;
        _recurrenceEndRow = _whenCard.AddRow(recurrenceEnd, rule: false);
        _recurrenceEndRow.Hidden = true;
        _recurrenceSummaryRow = _whenCard.AddRow(_recurrenceSummary, rule: false, minimumHeight: 0);
    }

    private void BuildAttendeesCard()
    {
        _attendeesCard.Title = Translator.CalendarEventCompose_Attendees;
        ConfigureInviteField();
        _inviteRow = _attendeesCard.AddRow(_invite, rule: false);
        _attendeesCard.AddRow(BuildInviteSuggestions(), rule: false, minimumHeight: 0, insets: 8);
        _attendeeList.Alignment = UIStackViewAlignment.Fill;
        _attendeesCard.AddRow(_attendeeList, rule: false, minimumHeight: 0, insets: 0);
        _attendeesNoteRow = _attendeesCard.AddRow(_attendeesNote, rule: false, minimumHeight: 0);
    }

    private void BuildDetailsCard()
    {
        _detailsCard.Title = Translator.CalendarEventDetails_Details;
        _reminderButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.ReminderOptions, ViewModel.SelectedReminderOption, option => option.DisplayText, option => ViewModel.SelectedReminderOption = option));
        _showAsButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.ShowAsOptions, ViewModel.SelectedShowAsOption, option => option.DisplayText, option => ViewModel.SelectedShowAsOption = option));
        _detailsCard.AddRow(ValueRow(Translator.CalendarEventDetails_Reminder, _reminderButton));
        _detailsCard.AddRow(ValueRow(Translator.CalendarEventDetails_ShowAs, _showAsButton));

        _privateSwitch.ValueChanged += (_, _) => ViewModel.IsPrivate = _privateSwitch.On;
        WinoAccessibility.Label(_privateSwitch, Translator.CalendarEventCompose_Private);
        WinoAccessibility.Help(_privateSwitch, Translator.CalendarEventCompose_PrivateTooltip);
        _detailsCard.AddRow(SwitchRow(WinoStyle.Label(Translator.CalendarEventCompose_Private, WinoStyle.Body), _privateSwitch));

        _onlineSwitch.ValueChanged += (_, _) => ViewModel.IsOnlineMeeting = _onlineSwitch.On;
        _onlineRow = _detailsCard.AddRow(SwitchRow(_onlineLabel, _onlineSwitch));
        _onlineInfoRow = _detailsCard.AddRow(WinoStyle.Label(Translator.CalendarEventCompose_OnlineMeetingInfo, WinoStyle.Description, WinoStyle.SecondaryText, 0), rule: false, minimumHeight: 0);
    }

    private void BuildAttachmentsCard()
    {
        _attachmentsCard.Title = Translator.CalendarEventDetails_Attachments;
        _attachmentList.Alignment = UIStackViewAlignment.Fill;
        _attachmentsCard.AddRow(_attachmentList, rule: false, minimumHeight: 0, insets: 0);
        var addBinding = Bindings.Own(new CommandBinding(ViewModel.AddAttachmentsCommand, () => null, enabled => _addAttachment.Enabled = enabled && ViewModel.CanAddAttachments, Dispatcher, ReportError));
        _addAttachment.PrimaryActionTriggered += (_, _) => addBinding.Execute();
        _attachmentsCard.AddRow(_addAttachment, rule: false, insets: 0);
        _attachmentsDisabledRow = _attachmentsCard.AddRow(_attachmentsDisabled, rule: false, minimumHeight: 0);
        _attachmentsDisabledRow.Hidden = true;
    }

    private void BuildNotesCard()
    {
        _notesCard.Title = Translator.CalendarEventCompose_Notes;
        _notesCard.ShowsChevron = false;
        _notesWebView.AccessibilityLabel = Translator.CalendarEventCompose_Notes;
        WinoLayout.Fill(_notesWebView, _notesHost, 0, 10, 4, 10);
        _notesHost.AddSubview(_notesSpinner);
        NSLayoutConstraint.ActivateConstraints(
        [
            _notesHost.HeightAnchor.ConstraintEqualTo((nfloat)NotesHeight),
            _notesSpinner.CenterXAnchor.ConstraintEqualTo(_notesHost.CenterXAnchor),
            _notesSpinner.CenterYAnchor.ConstraintEqualTo(_notesHost.CenterYAnchor)
        ]);
        _notesCard.AddRow(_notesHost, rule: false, insets: 0);
        _notesCard.SetOpen(true, false);
        _notesCard.HeaderTapped += (_, _) => FocusNotes();
    }

    /// <summary>Opens <paramref name="card"/> and closes the one that was open, or closes it when it is open.</summary>
    private void ToggleCard(ComposeCard card)
    {
        bool open = !card.IsOpen;
        bool animate = !UIAccessibility.IsReduceMotionEnabled;
        if (open) View?.EndEditing(true);
        foreach (var other in new[] { _whenCard, _attendeesCard, _detailsCard, _attachmentsCard })
            if (!ReferenceEquals(other, card) && other.IsOpen) other.SetOpen(false, animate);
        card.SetOpen(open, animate);
        if (!open) return;
        void Reveal()
        {
            _formScroll.LayoutIfNeeded();
            var frame = card.ConvertRectToView(card.Bounds, _formScroll);
            var visible = _formScroll.Bounds.Height - _formScroll.AdjustedContentInset.Top - _formScroll.AdjustedContentInset.Bottom;
            // The card's header comes to the top when the card is taller than the screen, otherwise the whole card shows.
            var target = frame.Height > visible ? new CGRect(frame.X, frame.Y - 8, frame.Width, visible) : frame.Inset(0, -8);
            _formScroll.ScrollRectToVisible(target, animate);
        }
        if (animate) UIView.Animate(0.25, () => { View?.LayoutIfNeeded(); }, Reveal);
        else Reveal();
    }

    /// <summary>Opens <paramref name="card"/> unless it is open already (the Debug bridge, focus moves).</summary>
    private void OpenCard(ComposeCard card)
    {
        if (!card.IsOpen) ToggleCard(card);
    }

    /// <summary>One wheel at a time: tapping Starts or Ends shows its wheel and hides the other.</summary>
    private void ToggleWheel(bool start)
    {
        if (_startPickerRow is null || _endPickerRow is null) return;
        View?.EndEditing(true);
        bool showStart = start && _startPickerRow.Hidden;
        bool showEnd = !start && _endPickerRow.Hidden;
        void Apply()
        {
            _startPickerRow.Hidden = !showStart;
            _endPickerRow.Hidden = !showEnd;
            ApplyValueButtons();
            View?.LayoutIfNeeded();
        }
        if (UIAccessibility.IsReduceMotionEnabled) Apply();
        else UIView.Animate(0.25, Apply);
    }

    // ---- Navigation bar ----

    private void BuildNavigationItems()
    {
        _cancelItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Cancel());
        var save = new UIBarButtonItem(Translator.Buttons_Save, UIBarButtonItemStyle.Done, (_, _) => Observe(SaveAsync()));
        _saveItem = save;
        _saveBinding = Bindings.Own(new CommandBinding(ViewModel.CreateCommand, () => null, enabled => save.Enabled = enabled, Dispatcher, ReportError));
        NavigationItem.Title = Translator.CalendarEventCompose_NewEventButton;
        NavigationItem.LeftBarButtonItem = _cancelItem;
        NavigationItem.RightBarButtonItem = save;
    }

    /// <summary>Cancel closes without running the ViewModel's Cancel (its navigation back would pop the shell history).</summary>
    private void Cancel()
    {
        if (_closing) return;
        _closing = true;
        CloseInviteSuggestions();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes an address still typed in the invite field (tapping Save does not end its editing), then creates the event.</summary>
    private async Task SaveAsync()
    {
        if (_closing || _saveBinding is not { } binding) return;
        CloseInviteSuggestions();
        var typed = _invite.TypedText;
        if (typed.Length > 0) await AddTypedAttendeesAsync(typed, notifyErrors: false, keepRejected: true);
        await Dispatcher.ExecuteOnUIThread(binding.Execute);
    }

    private void MarkEdited()
    {
        if (_edited || _released) return;
        _edited = true;
        ModalInPresentation = true;
        if (NavigationController is { } navigation) navigation.ModalInPresentation = true;
    }

    // ---- Lifecycle ----

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        var args = parameter as CalendarEventComposeNavigationArgs;
        _originalNotesHtml = args?.NotesHtml ?? string.Empty;
        ViewModel.GetHtmlNotesAsync = GetNotesHtmlAsync;

        var editor = new WebKitHtmlMailEditorSession(_notesWebView);
        _notesEditor = editor;
        editor.Configure(_launcher);
        editor.OperationFailed += NotesOperationFailed;
        editor.ContentChanged += NotesContentChanged;
        _formatToolbar?.Attach(editor);
        InstallKeyCommands();
#if DEBUG
        RegisterComposeDebugCommands();
#endif

        Bind(nameof(ViewModel.Title), vm => vm.Title, value =>
        {
            if ((_title.Text ?? string.Empty) != (value ?? string.Empty) && _title.MarkedTextRange is null) _title.Text = value ?? string.Empty;
        });
        Bind(nameof(ViewModel.Location), vm => vm.Location, value =>
        {
            if ((_location.Text ?? string.Empty) != (value ?? string.Empty) && _location.MarkedTextRange is null) _location.Text = value ?? string.Empty;
        });
        Bind(nameof(ViewModel.IsAllDay), vm => vm.IsAllDay, value =>
        {
            _allDaySwitch.On = value;
            _startPicker.Mode = value ? UIDatePickerMode.Date : UIDatePickerMode.DateAndTime;
            _endPicker.Mode = _startPicker.Mode;
            if (_timeZoneRow is { } zone) zone.Hidden = value;
            ApplyLocalTimeHint();
            PullDates();
        });
        Bind(nameof(ViewModel.SelectedTimeZoneOption), vm => vm.SelectedTimeZoneOption, option => { SetMenuTitle(_timeZoneButton, option?.DisplayText); ApplyWhenSummary(); });
        Bind(nameof(ViewModel.LocalTimeHintText), vm => vm.LocalTimeHintText, _ => ApplyLocalTimeHint());
        Bind(nameof(ViewModel.IsPrivate), vm => vm.IsPrivate, value => { _privateSwitch.On = value; ApplyDetailsSummary(); });
        Bind(nameof(ViewModel.IsOnlineMeeting), vm => vm.IsOnlineMeeting, value => { _onlineSwitch.On = value; ApplyDetailsSummary(); });
        Bind(nameof(ViewModel.StartDate), vm => vm.StartDate, _ => { PullDates(); ApplyRepeatTitle(); });
        Bind(nameof(ViewModel.StartTime), vm => vm.StartTime, _ => PullDates());
        Bind(nameof(ViewModel.EndDate), vm => vm.EndDate, _ => PullDates());
        Bind(nameof(ViewModel.EndTime), vm => vm.EndTime, _ => PullDates());
        Bind(nameof(ViewModel.DurationText), vm => vm.DurationText, _ => ApplyDuration());
        Bind(nameof(ViewModel.IsDateRangeValid), vm => vm.IsDateRangeValid, _ => ApplyDuration());
        Bind(nameof(ViewModel.RecurrenceSummary), vm => vm.RecurrenceSummary, value =>
        {
            _recurrenceSummary.Text = value ?? string.Empty;
            if (_recurrenceSummaryRow is { } row) row.Hidden = string.IsNullOrEmpty(value);
            ApplyWhenSummary();
        });
        Bind(nameof(ViewModel.SelectedRepeatOption), vm => vm.SelectedRepeatOption, _ => { ApplyRepeatTitle(); ApplyWhenSummary(); });
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, option => { SetMenuTitle(_showAsButton, option?.DisplayText); ApplyDetailsSummary(); });
        Bind(nameof(ViewModel.SelectedReminderOption), vm => vm.SelectedReminderOption, option => { SetMenuTitle(_reminderButton, option?.DisplayText); ApplyDetailsSummary(); });
        Bind(nameof(ViewModel.SelectedCalendar), vm => vm.SelectedCalendar, _ => { ApplyCalendar(); ApplyAttendees(); ApplyAttachments(); });
        Bind(nameof(ViewModel.CanAddOnlineMeeting), vm => vm.CanAddOnlineMeeting, _ => ApplyCalendar());
        Bind(nameof(ViewModel.CanAddAttachments), vm => vm.CanAddAttachments, _ => ApplyCalendar());
        Bind(nameof(ViewModel.SaveButtonText), vm => vm.SaveButtonText, text => { if (_saveItem is { } save) save.Title = string.IsNullOrEmpty(text) ? Translator.Buttons_Save : text; });
        Bind(nameof(ViewModel.AttendeeCount), vm => vm.AttendeeCount, _ => ApplyAttendees());
        Bind(nameof(ViewModel.AttachmentCount), vm => vm.AttachmentCount, _ => ApplyAttachments());
        Bind(nameof(ViewModel.LastCreatedResult), vm => vm.LastCreatedResult, result =>
        {
            if (result is null || _closing) return;
            _closing = true;
            CloseInviteSuggestions();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        BindRecurrence();

        try { await ViewModel.InitializeNavigationAsync(mode, parameter); }
        catch (Exception exception) { ReportError(exception); }

        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            ApplyCalendar();
            ApplyAttendees();
            ApplyAttachments();
            // Changes from here on are the user's (loading the navigation arguments is not an edit).
            PropertyChangedEventHandler changed = (_, changedArgs) =>
            {
                if (changedArgs.PropertyName is { } name && EditedProperties.Contains(name)) BeginInvokeOnMainThread(MarkEdited);
            };
            ViewModel.PropertyChanged += changed;
            Bindings.Own(new ActionDisposable(() => ViewModel.PropertyChanged -= changed));
            // Quick-event input carried over is unsaved work from the start.
            if (args is { HasPendingInput: true }) MarkEdited();
            if (string.IsNullOrEmpty(ViewModel.Title)) _title.BecomeFirstResponder();
        });
        Observe(LoadNotesAsync());
    }

    protected override async Task DeactivateAsync()
    {
        _released = true;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            ReleaseInvite();
            _formatToolbar?.Detach();
            _notesWebView.Accessory = null;
        });
        await DisposeNotesEditorAsync();
        await base.DeactivateAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            ReleaseInvite();
            _formatToolbar?.Detach();
            _notesWebView.Accessory = null;
            if (_notesEditor is not null && !_notesDisposed) Observe(DisposeNotesEditorAsync());
        }
        base.Dispose(disposing);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { if (!_released) ReportError(exception); }
    }

    // ---- Keyboard shortcuts (iPad hardware keyboard) ----

    private void InstallKeyCommands()
    {
        if (_keyCommandsInstalled) return;
        _keyCommandsInstalled = true;
        // ⌘↩ like the mail composer: a plain Return belongs to the notes editor.
        AddKeyCommand(KeyCommand("\r", UIKeyModifierFlags.Command, "calendarComposeSave:", Translator.Buttons_Save));
        AddKeyCommand(KeyCommand(UIKeyCommand.Escape.ToString(), 0, "calendarComposeEscape:", null));
    }

    private static UIKeyCommand KeyCommand(string input, UIKeyModifierFlags modifiers, string selector, string? title)
    {
        var command = UIKeyCommand.Create(new NSString(input), modifiers, new Selector(selector));
        if (title is not null) command.DiscoverabilityTitle = new NSString(title);
        command.WantsPriorityOverSystemBehavior = true;
        return command;
    }

    [Export("calendarComposeSave:")]
    private void SaveKey(UIKeyCommand command) => Observe(SaveAsync());

    /// <summary>Esc closes the suggestion list when it is open, otherwise cancels (Mac Cancel key equivalent).</summary>
    [Export("calendarComposeEscape:")]
    private void EscapeKey(UIKeyCommand command)
    {
        if (_inviteSuggestions is { IsVisible: true })
        {
            CloseInviteSuggestions();
            return;
        }
        Cancel();
    }

    // ---- Notes ----

    private void BuildEditorToolbar()
    {
        var toolbar = new EditorFormatToolbar(_preferences, _translations, ReportError, () => Presenter, PickInlineImagesAsync, () => _notesWebView.EndEditing(true));
        _formatToolbar = toolbar;
        _themeButton = new EditorToolbarButton(WinoIconGlyph.DarkEditor, Translator.Composer_DarkTheme, ToggleEditorTheme);
        toolbar.AddOptionsItems(
            new EditorToolbarItem(_themeButton),
            new EditorToolbarItem(new EditorToolbarButton(WinoIconGlyph.ArrowUndo, Translator.MacOSMenu_Undo, () => toolbar.Run(EditorCommand.Undo()))),
            new EditorToolbarItem(new EditorToolbarButton(WinoIconGlyph.ArrowRedo, Translator.MacOSMenu_Redo, () => toolbar.Run(EditorCommand.Redo()))));
        _notesWebView.Accessory = toolbar;
    }

    private UIViewController Presenter => PresentedViewController is { IsBeingDismissed: false } presented ? presented : this;

    private async Task<List<SharedFile>> PickInlineImagesAsync()
        => _dialogs is { } dialogs ? await dialogs.PickFilesAsync(EditorDialogs.ImageFilters) ?? [] : [];

    private void FocusNotes()
    {
        if (_notesEditor is not { } editor || _notesDisposed || !_notesReady) return;
        _notesWebView.BecomeFirstResponder();
        Observe(editor.FocusEditorAsync(true));
    }

    /// <summary>The composer typography, spell check and autocorrect (Mac WinoRichTextEditorView.LoadAsync), then the notes.</summary>
    private async Task LoadNotesAsync()
    {
        if (_notesEditor is not { } editor) return;
        var dark = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _notesSpinner.StartAnimating();
            dark = WinoTraits.IsDark(TraitCollection);
            _darkEditor = dark;
            _appearanceDark = dark;
            UpdateThemeButton();
        });
        try
        {
            await editor.SetLinkLabelsAsync(Translator.Composer_OpenLink, Translator.Composer_RemoveLink);
            await editor.InitializeAsync();
            await editor.SetThemeAsync(dark);
            await editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
            await editor.Session.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(_preferences.IsComposerSpellCheckEnabled));
            if (!string.IsNullOrWhiteSpace(_preferences.ComposerSpellCheckLanguageCode))
                await editor.Session.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(_preferences.ComposerSpellCheckLanguageCode));
            await editor.Session.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(_preferences.IsComposerAutoCorrectEnabled));
            await editor.RenderHtmlAsync(_originalNotesHtml);
            string? baseline = null;
            try { baseline = await editor.GetHtmlBodyAsync(); }
            catch (Exception exception) { if (!_released) ReportError(exception); }
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _baselineNotesBody = baseline;
                _notesReady = true;
                _notesSpinner.StopAnimating();
            });
        }
        catch (Exception exception)
        {
            if (_released) return;
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(ShowNotesFallback);
        }
    }

    private void NotesContentChanged(object? sender, EventArgs args)
    {
        if (!_notesReady) return;
        _notesEdited = true;
        BeginInvokeOnMainThread(MarkEdited);
    }

    private void NotesOperationFailed(object? sender, Exception exception)
    {
        if (!_released) ReportError(exception);
    }

    private async Task<string> GetNotesHtmlAsync()
    {
        if (!_notesReady || _notesFailed || _notesDisposed || _notesEditor is not { } editor) return _originalNotesHtml;
        string? current = null;
        try { current = await editor.GetHtmlBodyAsync(); }
        catch (Exception exception) { ReportError(exception); }
        return CalendarNotesHtmlResolver.Resolve(_originalNotesHtml, _baselineNotesBody, current, _notesReady, _notesEdited);
    }

    /// <summary>The editor failed to load: show the notes as read-only text and save the original HTML unchanged.</summary>
    private void ShowNotesFallback()
    {
        if (_released) return;
        _notesFailed = true;
        _notesSpinner.StopAnimating();
        _notesWebView.Hidden = true;
        var text = new UITextView
        {
            Editable = false,
            Selectable = true,
            Font = WinoStyle.Body,
            AdjustsFontForContentSizeCategory = true,
            BackgroundColor = UIColor.Clear,
            Text = StripHtml(_originalNotesHtml),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(text, _notesHost, 0, 12, 8, 12);
    }

    private async Task DisposeNotesEditorAsync()
    {
        if (_notesEditor is not { } editor || _notesDisposed) return;
        _notesDisposed = true;
        editor.ContentChanged -= NotesContentChanged;
        editor.OperationFailed -= NotesOperationFailed;
        // Disposes the session even when it never became ready (idempotent).
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleEditorTheme()
    {
        if (_notesEditor is not { } editor || _notesDisposed) return;
        _darkEditor = !_darkEditor;
        UpdateThemeButton();
        Observe(editor.SetThemeAsync(_darkEditor));
    }

    /// <summary>The button offers the other theme (Windows GetEditorThemeIcon / GetEditorThemeToolTip).</summary>
    private void UpdateThemeButton()
        => _themeButton?.SetGlyph(_darkEditor ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(_darkEditor));

    /// <summary>A light or dark appearance change re-themes the editor; a theme picked with the toggle stays until then.</summary>
    private void FollowAppearance()
    {
        if (!IsViewLoaded) return;
        var dark = WinoTraits.IsDark(TraitCollection);
        var previous = _appearanceDark;
        _appearanceDark = dark;
        if (previous is null || previous == dark || _notesEditor is not { } editor || _notesDisposed) return;
        _darkEditor = dark;
        UpdateThemeButton();
        Observe(editor.SetThemeAsync(dark));
    }

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>|</p>|</div>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    // ---- Calendar, time zone and option menus ----

    /// <summary>Writable calendars grouped by account, each with its colour (the ViewModel already offers only writable ones).</summary>
    private UIMenuElement[] BuildCalendarMenu()
    {
        var selected = ViewModel.SelectedCalendar;
        var sections = new List<UIMenuElement>();
        foreach (var group in ViewModel.AvailableCalendarGroups)
        {
            var calendars = group.AccountCalendars.Where(calendar => !calendar.IsReadOnly || ReferenceEquals(calendar, selected)).ToList();
            if (calendars.Count == 0) continue;
            var items = calendars.Select(calendar => (UIMenuElement)EditorFormatToolbar.Action(calendar.Name ?? string.Empty,
                () => ViewModel.SelectedCalendar = calendar, ReferenceEquals(calendar, selected), true, ColorDot(CalendarColor(calendar)))).ToArray();
            var account = group.Account;
            var title = account is null ? string.Empty : $"{account.Name} ({account.Address})";
            sections.Add(EditorFormatToolbar.Inline(items, title));
        }
        return sections.ToArray();
    }

    /// <summary>Windows time zone ComboBox; the local zone is repeated on top so it is one tap away.</summary>
    private UIMenuElement[] BuildTimeZoneMenu()
    {
        var selected = ViewModel.SelectedTimeZoneOption;
        var sections = new List<UIMenuElement>();
        var local = ViewModel.TimeZoneOptions.FirstOrDefault(option => option.Id == TimeZoneInfo.Local.Id);
        if (local is not null)
            sections.Add(EditorFormatToolbar.Inline(new UIMenuElement[] { EditorFormatToolbar.Action(local.DisplayText, () => SelectTimeZone(local), ReferenceEquals(local, selected)) }));
        sections.Add(EditorFormatToolbar.Inline(ViewModel.TimeZoneOptions
            .Select(option => (UIMenuElement)EditorFormatToolbar.Action(option.DisplayText, () => SelectTimeZone(option), ReferenceEquals(option, selected)))));
        return sections.ToArray();
    }

    private void SelectTimeZone(CalendarComposeTimeZoneOption option)
    {
        if (!ReferenceEquals(option, ViewModel.SelectedTimeZoneOption)) ViewModel.SelectedTimeZoneOption = option;
    }

    private static UIMenuElement[] OptionMenu<T>(IList<T> options, T? selected, Func<T, string> text, Action<T> choose) where T : class
        => options.Select(option => (UIMenuElement)EditorFormatToolbar.Action(text(option) ?? string.Empty, () => choose(option), ReferenceEquals(option, selected))).ToArray();

    /// <summary>A pull-down menu built when it opens, so its checked item and texts are always current.</summary>
    private static UIMenu DeferredMenu(Func<UIMenuElement[]> build)
        => UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
            [UIDeferredMenuElement.CreateUncached(completion => completion(build()))]);

    private void ApplyRepeatTitle() => SetMenuTitle(_repeatButton, ViewModel.SelectedRepeatOption?.DisplayText);

    private static UIColor CalendarColor(AccountCalendarViewModel calendar)
        => WinoStyle.FromHexString(calendar.BackgroundColorHex) ?? WinoStyle.Accent;

    private void ApplyCalendar()
    {
        var selected = ViewModel.SelectedCalendar;
        var color = selected is null ? WinoStyle.TertiaryText : CalendarColor(selected);
        _calendarBar.BackgroundColor = color;
        var configuration = _calendarButton.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = selected?.Name ?? Translator.CalendarEventCompose_SelectCalendar;
        configuration.AttributedSubtitle = selected?.Account?.Address is { Length: > 0 } address
            ? new NSAttributedString(address, new UIStringAttributes { ForegroundColor = WinoStyle.SecondaryText, Font = WinoStyle.Description })
            : null;
        _calendarButton.Configuration = configuration;
        _calendarButton.Enabled = ViewModel.CanChangeCalendar;
        _calendarButton.AccessibilityValue = selected is null ? null : $"{selected.Name}, {selected.Account?.Address}";
        _onlineLabel.Text = string.IsNullOrEmpty(ViewModel.OnlineMeetingProviderText) ? Translator.CalendarEventCompose_OnlineMeetingInfoTitle : ViewModel.OnlineMeetingProviderText;
        WinoAccessibility.Label(_onlineSwitch, _onlineLabel.Text);
        if (_onlineRow is { } online) online.Hidden = !ViewModel.CanAddOnlineMeeting;
        if (_onlineInfoRow is { } info) info.Hidden = !ViewModel.CanAddOnlineMeeting;
        var canAttach = ViewModel.CanAddAttachments;
        _addAttachment.Enabled = canAttach && ViewModel.AddAttachmentsCommand.CanExecute(null);
        _attachmentsDisabled.Text = canAttach ? string.Empty : ViewModel.AttachmentsDisabledTooltipText ?? string.Empty;
        if (_attachmentsDisabledRow is { } disabled) disabled.Hidden = canAttach || string.IsNullOrEmpty(_attachmentsDisabled.Text);
        ApplyDetailsSummary();
    }

    /// <summary>The Details card's closed line: reminder, show as, then Private and the online meeting when they are on.</summary>
    private void ApplyDetailsSummary()
    {
        var parts = new List<string?> { ViewModel.SelectedReminderOption?.DisplayText, ViewModel.SelectedShowAsOption?.DisplayText };
        if (ViewModel.IsPrivate) parts.Add(Translator.CalendarEventCompose_Private);
        if (ViewModel.IsOnlineMeeting && ViewModel.CanAddOnlineMeeting) parts.Add(_onlineLabel.Text);
        _detailsCard.Caption = Join(parts);
    }

    private static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static UIImage ColorDot(UIColor color)
    {
        using var renderer = new UIGraphicsImageRenderer(new CGSize(12, 12));
        var image = renderer.CreateImage(_ =>
        {
            color.SetFill();
            UIBezierPath.FromOval(new CGRect(1, 1, 10, 10)).Fill();
        });
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }

    // ---- When ----

    private void ApplyDuration()
    {
        bool valid = ViewModel.IsDateRangeValid;
        _rangeError.Text = valid ? string.Empty : ViewModel.DateRangeErrorText ?? string.Empty;
        _rangeError.Hidden = valid;
        _duration.Text = ViewModel.HasEndDayOffset ? $"{ViewModel.DurationText} · {ViewModel.EndDayOffsetText}" : ViewModel.DurationText ?? string.Empty;
        _duration.Hidden = string.IsNullOrEmpty(_duration.Text);
        if (_durationRow is { } row) row.Hidden = _duration.Hidden && _rangeError.Hidden;
        ApplyWhenSummary();
    }

    private void ApplyLocalTimeHint()
    {
        var text = ViewModel.IsAllDay ? string.Empty : ViewModel.LocalTimeHintText ?? string.Empty;
        _localTimeHint.Text = text;
        if (_localTimeHintRow is { } row) row.Hidden = string.IsNullOrEmpty(text);
    }

    private void PullDates()
    {
        _applying = true;
        try
        {
            bool allDay = ViewModel.IsAllDay;
            _startPicker.Date = ToNSDate(ViewModel.StartDate.Date + (allDay ? TimeSpan.Zero : ViewModel.StartTime));
            _endPicker.Date = ToNSDate(ViewModel.EndDate.Date + (allDay ? TimeSpan.Zero : ViewModel.EndTime));
        }
        finally { _applying = false; }
        ApplyValueButtons();
        ApplyWhenSummary();
    }

    private void PushDates()
    {
        if (_applying) return;
        var start = FromNSDate(_startPicker.Date);
        var end = FromNSDate(_endPicker.Date);
        if (ViewModel.StartDate.Date != start.Date) ViewModel.StartDate = new DateTimeOffset(start.Date, TimeZoneInfo.Local.GetUtcOffset(start.Date));
        if (ViewModel.EndDate.Date != end.Date) ViewModel.EndDate = new DateTimeOffset(end.Date, TimeZoneInfo.Local.GetUtcOffset(end.Date));
        // All-day wheels show dates only; the times they would report are midnight, not the user's choice.
        if (ViewModel.IsAllDay) return;
        if (ViewModel.StartTime != start.TimeOfDay) ViewModel.StartTime = start.TimeOfDay;
        if (ViewModel.EndTime != end.TimeOfDay) ViewModel.EndTime = end.TimeOfDay;
    }

    private DateTime StartLocal => ViewModel.StartDate.Date + (ViewModel.IsAllDay ? TimeSpan.Zero : ViewModel.StartTime);
    private DateTime EndLocal => ViewModel.EndDate.Date + (ViewModel.IsAllDay ? TimeSpan.Zero : ViewModel.EndTime);

    private static NSDateFormatter CreateDayFormatter()
    {
        var formatter = new NSDateFormatter();
        formatter.SetLocalizedDateFormatFromTemplate("EEEMMMd");
        return formatter;
    }

    private static string Day(DateTime value) => DayFormatter.ToString(ToNSDate(value));
    private static string Time(DateTime value) => TimeFormatter.ToString(ToNSDate(value));

    /// <summary>The Starts and Ends values; the one whose wheel is open is tinted.</summary>
    private void ApplyValueButtons()
    {
        bool allDay = ViewModel.IsAllDay;
        SetValue(_startValue, allDay ? Day(StartLocal) : $"{Day(StartLocal)}  {Time(StartLocal)}", _startPickerRow is { Hidden: false });
        SetValue(_endValue, allDay ? Day(EndLocal) : $"{Day(EndLocal)}  {Time(EndLocal)}", _endPickerRow is { Hidden: false });
    }

    private static void SetValue(UIButton button, string text, bool active)
    {
        var configuration = button.Configuration ?? UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = text;
        configuration.BaseForegroundColor = active ? WinoStyle.Accent : WinoStyle.PrimaryText;
        button.Configuration = configuration;
        button.AccessibilityValue = text;
    }

    /// <summary>The When card's closed lines: "Tue, Nov 3 · 10:00 – 10:30 AM", then duration, time zone and repeat.</summary>
    private void ApplyWhenSummary()
    {
        DateTime start = StartLocal, end = EndLocal;
        _whenCard.Title = ViewModel.IsAllDay
            ? start.Date == end.Date ? Day(start) : $"{Day(start)} – {Day(end)}"
            : start.Date == end.Date ? $"{Day(start)} · {Time(start)} – {Time(end)}" : $"{Day(start)} {Time(start)} – {Day(end)} {Time(end)}";
        var duration = ViewModel.HasEndDayOffset ? $"{ViewModel.DurationText} {ViewModel.EndDayOffsetText}" : ViewModel.DurationText;
        _whenCard.Caption = Join(
        [
            ViewModel.IsDateRangeValid ? duration : ViewModel.DateRangeErrorText,
            ViewModel.IsAllDay ? null : ViewModel.SelectedTimeZoneOption?.DisplayText,
            ViewModel.SelectedRepeatOption?.DisplayText
        ]);
        _whenCard.CaptionColor = ViewModel.IsDateRangeValid ? WinoStyle.SecondaryText : WinoStyle.Critical;
    }

    private static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static NSDate ToNSDate(DateTime local) => NSDate.FromTimeIntervalSinceReferenceDate((DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime() - Reference).TotalSeconds);
    private static DateTime FromNSDate(NSDate date) => Reference.AddSeconds(date.SecondsSinceReferenceDate).ToLocalTime();

    // ---- Attendees and attachments lists ----

    private void ApplyAttendees()
    {
        Clear(_attendeeList);
        _attendeesCard.Title = ViewModel.HasAttendees ? $"{Translator.CalendarEventCompose_Attendees} ({ViewModel.AttendeeCount})" : Translator.CalendarEventCompose_Attendees;
        _attendeesCard.Caption = ViewModel.HasAttendees
            ? string.Join(", ", ViewModel.Attendees.Select(static attendee => string.IsNullOrWhiteSpace(attendee.DisplayName) ? attendee.Email : attendee.DisplayName))
            : Translator.CalendarEventDetails_InviteSomeone;
        if (_inviteRow is { } invite) invite.Hidden = !ViewModel.CanAddAttendees;
        var notes = new List<string?>();
        if (ViewModel.HasInvitationsNotSentWarning) notes.Add(ViewModel.InvitationsNotSentText);
        if (ViewModel.HasAttendeeLimit) notes.Add(ViewModel.AttendeeLimitText);
        _attendeesNote.Text = string.Join("\n", notes.Where(static note => !string.IsNullOrWhiteSpace(note)));
        if (_attendeesNoteRow is { } noteRow) noteRow.Hidden = string.IsNullOrEmpty(_attendeesNote.Text);
        // The organizer row like Windows.
        if (ViewModel.SelectedCalendar is not null)
            _attendeeList.AddArrangedSubview(PersonRow(ViewModel.OrganizerDisplayName, ViewModel.OrganizerAddress, Translator.CalendarEventCompose_Organizer, null, null));
        foreach (var attendee in ViewModel.Attendees)
        {
            var captured = attendee;
            _attendeeList.AddArrangedSubview(PersonRow(attendee.DisplayName, attendee.Email, attendee.AttendanceTypeText,
                () =>
                {
                    ViewModel.ToggleAttendeeOptionalCommand.Execute(captured);
                    MarkEdited();
                    // The badge follows Required / Optional.
                    ApplyAttendees();
                },
                () => ViewModel.RemoveAttendeeCommand.Execute(captured)));
        }
    }

    private static UIView PersonRow(string? name, string? address, string badge, Action? toggle, Action? remove)
    {
        var shown = string.IsNullOrWhiteSpace(name) ? address ?? string.Empty : name;
        var picture = new WinoContactPicture(32);
        picture.SetIdentity(shown, address);
        var nameLabel = WinoStyle.Label(shown, WinoStyle.BodyMedium);
        var addressLabel = WinoStyle.Label(address ?? string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(1, nameLabel, addressLabel);
        text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);

        var badgeConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        badgeConfiguration.Title = badge;
        badgeConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        badgeConfiguration.BaseForegroundColor = toggle is null ? WinoStyle.SecondaryText : WinoStyle.Accent;
        var badgeButton = new UIButton { Configuration = badgeConfiguration, TranslatesAutoresizingMaskIntoConstraints = false, Enabled = toggle is not null };
        badgeButton.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        badgeButton.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        if (toggle is not null)
        {
            badgeButton.AccessibilityHint = Translator.CalendarEventCompose_AttendeeTypeTooltip;
            badgeButton.PrimaryActionTriggered += (_, _) => toggle();
        }

        var children = new List<UIView> { picture, text, badgeButton };
        if (remove is not null) children.Add(RemoveButton(Translator.CalendarEventCompose_RemoveAttendee, remove));
        var row = WinoLayout.HStack(10, children.ToArray());
        row.Alignment = UIStackViewAlignment.Center;
        return ComposeCard.Row(row, rule: true, minimumHeight: 52);
    }

    private void ApplyAttachments()
    {
        Clear(_attachmentList);
        _attachmentsCard.Title = ViewModel.HasAttachments ? $"{Translator.CalendarEventDetails_Attachments} ({ViewModel.AttachmentCount})" : Translator.CalendarEventDetails_Attachments;
        _attachmentsCard.Caption = ViewModel.HasAttachments
            ? string.Join(", ", ViewModel.Attachments.Select(static attachment => attachment.FileName))
            : ViewModel.CanAddAttachments ? Translator.CalendarEventCompose_AddAttachment : ViewModel.AttachmentsDisabledTooltipText ?? string.Empty;
        foreach (var attachment in ViewModel.Attachments)
        {
            var captured = attachment;
            var icon = new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText);
            var name = WinoStyle.Label(attachment.FileName, WinoStyle.Body);
            name.LineBreakMode = UILineBreakMode.MiddleTruncation;
            name.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
            name.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.TertiaryText);
            size.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
            var remove = RemoveButton(Translator.CalendarEventCompose_RemoveAttachment, () => ViewModel.RemoveAttachmentCommand.Execute(captured));
            var row = WinoLayout.HStack(8, icon, name, size, remove);
            row.Alignment = UIStackViewAlignment.Center;
            _attachmentList.AddArrangedSubview(ComposeCard.Row(row, rule: _attachmentList.ArrangedSubviews.Length > 0));
        }
    }

    private static UIButton RemoveButton(string label, Action remove)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12, null, label);
        configuration.BaseForegroundColor = WinoStyle.SecondaryText;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = label };
        WinoLayout.Size(button, WinoStyle.MinimumHitTarget, WinoStyle.MinimumHitTarget);
        button.PrimaryActionTriggered += (_, _) => remove();
        return button;
    }

    private static void Clear(UIStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews.ToArray())
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    // ---- View helpers ----

    /// <summary>A trailing pull-down value (the Settings value row): secondary text with the pop-up indicator.</summary>
    private static UIButton MenuButton(string accessibilityLabel)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.BaseForegroundColor = WinoStyle.SecondaryText;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 8, 6, 0);
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = accessibilityLabel };
        button.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        return button;
    }

    private static void SetMenuTitle(UIButton button, string? title, UIImage? image = null)
    {
        var configuration = button.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title ?? string.Empty;
        configuration.Image = image;
        button.Configuration = configuration;
        button.AccessibilityValue = title;
    }

    /// <summary>The calendar row: glyph, calendar name over its account address, and the pop-up indicator.</summary>
    private static UIButton CalendarButton()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.TemplateImage(WinoIconGlyph.Calendar, 18);
        configuration.ImagePadding = 14;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(8, 16, 8, 16);
        configuration.TitleAlignment = UIButtonConfigurationTitleAlignment.Leading;
        var button = new UIButton
        {
            Configuration = configuration,
            ShowsMenuAsPrimaryAction = true,
            HorizontalAlignment = UIControlContentHorizontalAlignment.Leading,
            TranslatesAutoresizingMaskIntoConstraints = false,
            AccessibilityLabel = Translator.CalendarEventCompose_SelectCalendar
        };
        button.ConfigurationUpdateHandler = static candidate =>
        {
            if (candidate.Configuration is not { } current) return;
            current.ImageColorTransformer = _ => WinoStyle.SecondaryText;
            candidate.Configuration = current;
        };
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(52).Active = true;
        return button;
    }

    /// <summary>The Starts / Ends value: a grey pill that opens the row's wheel.</summary>
    private static UIButton ValueButton(string accessibilityLabel)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = accessibilityLabel };
        button.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        return button;
    }

    /// <summary>A full-width accent action row (Add attachment).</summary>
    private static UIButton ActionButton(string title, WinoIconGlyph glyph)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.Image = WinoIcons.TemplateImage(glyph, 16);
        configuration.ImagePadding = 12;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(12, 16, 12, 16);
        var button = new UIButton { Configuration = configuration, HorizontalAlignment = UIControlContentHorizontalAlignment.Leading, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return button;
    }

    /// <summary>One date-and-time wheel (one gesture sets both); the all-day binding switches it to dates only.</summary>
    private static UIDatePicker WheelPicker(string accessibilityLabel)
    {
        var picker = new UIDatePicker { Mode = UIDatePickerMode.DateAndTime, PreferredDatePickerStyle = UIDatePickerStyle.Wheels, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(picker, accessibilityLabel);
        return picker;
    }

    private static UIDatePicker DatePicker(UIDatePickerMode mode, string accessibilityLabel)
    {
        var picker = new UIDatePicker { Mode = mode, PreferredDatePickerStyle = UIDatePickerStyle.Compact, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(picker, accessibilityLabel);
        picker.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        return picker;
    }

    /// <summary>The view at the start of a full-width row, the rest left empty.</summary>
    private static UIStackView Leading(UIView view)
    {
        var row = WinoLayout.HStack(0, view, WinoLayout.Spacer());
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>The label on the leading side and a switch on the trailing side.</summary>
    private static UIStackView SwitchRow(UILabel label, UISwitch control)
    {
        label.IsAccessibilityElement = false;
        label.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        label.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        control.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(12, label, control);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>A label on the leading side and its value control on the trailing side (Settings value row).</summary>
    private static UIStackView ValueRow(string title, UIView value)
    {
        var label = WinoStyle.Label(title, WinoStyle.Body);
        label.IsAccessibilityElement = false;
        label.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        label.SetContentCompressionResistancePriority(750, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(12, label, value);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>A secondary glyph in the leading column beside <paramref name="content"/> (location, attachments).</summary>
    private static UIStackView GlyphLine(WinoIconGlyph glyph, UIView content)
    {
        var icon = new WinoIconView(glyph, 18, WinoStyle.SecondaryText) { TranslatesAutoresizingMaskIntoConstraints = false, IsAccessibilityElement = false };
        WinoLayout.Size(icon, 20, 20);
        content.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(14, icon, content);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>
    /// An inset card on the grouped background (the Settings group surface). With a glyph it has a header row: the glyph,
    /// a bold one-line summary, a caption and a chevron; tapping the header raises <see cref="HeaderTapped"/> and the
    /// owner opens or closes the rows under it. Without a glyph it is a plain group that is always open.
    /// </summary>
    private sealed class ComposeCard : WinoSurfaceView
    {
        private readonly UIStackView _stack = WinoLayout.VStack(0);
        private readonly UIStackView _rows = WinoLayout.VStack(0);
        private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        private readonly UILabel _caption = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        private readonly WinoIconView _chevron = new(WinoIconGlyph.ChevronDown, 12, WinoStyle.TertiaryText) { TranslatesAutoresizingMaskIntoConstraints = false };
        private readonly UIControl? _header;
        private bool _hasRows;

        public ComposeCard(WinoIconGlyph? glyph)
        {
            Fill = WinoSettingsStyle.GroupFill;
            Stroke = WinoStyle.ZoneStroke;
            CornerRadius = WinoStyle.ZoneRadius;
            _stack.Alignment = UIStackViewAlignment.Fill;
            _rows.Alignment = UIStackViewAlignment.Fill;
            WinoLayout.Fill(_stack, this);
            if (glyph is { } icon)
            {
                var header = new UIControl { TranslatesAutoresizingMaskIntoConstraints = false, IsAccessibilityElement = true, AccessibilityTraits = UIAccessibilityTrait.Button | UIAccessibilityTrait.Header };
                var glyphView = new WinoIconView(icon, 20, WinoStyle.SecondaryText) { TranslatesAutoresizingMaskIntoConstraints = false, UserInteractionEnabled = false };
                WinoLayout.Size(glyphView, 22, 22);
                WinoLayout.Size(_chevron, 14, 14);
                _caption.LineBreakMode = UILineBreakMode.TailTruncation;
                var text = WinoLayout.VStack(2, _title, _caption);
                text.UserInteractionEnabled = false;
                text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
                text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
                var line = WinoLayout.HStack(14, glyphView, text, _chevron);
                line.Alignment = UIStackViewAlignment.Center;
                line.UserInteractionEnabled = false;
                WinoLayout.Fill(line, header, 10, 16, 10, 16);
                header.HeightAnchor.ConstraintGreaterThanOrEqualTo(60).Active = true;
                header.TouchUpInside += (_, _) => HeaderTapped?.Invoke(this, EventArgs.Empty);
                header.TouchDown += (_, _) => header.BackgroundColor = WinoStyle.SelectedFill;
                header.TouchUpInside += (_, _) => header.BackgroundColor = UIColor.Clear;
                header.TouchUpOutside += (_, _) => header.BackgroundColor = UIColor.Clear;
                header.TouchCancel += (_, _) => header.BackgroundColor = UIColor.Clear;
                _header = header;
                _stack.AddArrangedSubview(header);
                _rows.Hidden = true;
            }
            _stack.AddArrangedSubview(_rows);
        }

        public event EventHandler? HeaderTapped;

        public bool IsOpen => !_rows.Hidden;

        public string Title
        {
            get => _title.Text ?? string.Empty;
            set { _title.Text = value; UpdateAccessibility(); }
        }

        public string Caption
        {
            get => _caption.Text ?? string.Empty;
            set { _caption.Text = value; _caption.Hidden = string.IsNullOrEmpty(value); UpdateAccessibility(); }
        }

        public UIColor CaptionColor { set => _caption.TextColor = value; }

        public bool ShowsChevron { set => _chevron.Hidden = !value; }

        /// <summary>Adds a row: <paramref name="content"/> inset by 16pt, with a hairline above it unless it is the first.</summary>
        public UIView AddRow(UIView content, bool rule = true, double minimumHeight = WinoStyle.MinimumHitTarget, double insets = 16)
        {
            var row = Row(content, rule && (_hasRows || _header is not null), minimumHeight, insets);
            _hasRows = true;
            _rows.AddArrangedSubview(row);
            return row;
        }

        /// <summary>A card row; <paramref name="insets"/> 0 lets content that draws its own padding fill the row.</summary>
        public static UIView Row(UIView content, bool rule = true, double minimumHeight = WinoStyle.MinimumHitTarget, double insets = 16)
        {
            var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
            double vertical = insets == 0 ? 0 : 6;
            WinoLayout.Fill(content, row, vertical, insets, vertical, insets);
            if (minimumHeight > 0) row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)minimumHeight).Active = true;
            if (rule)
            {
                var line = new WinoSeparator { TranslatesAutoresizingMaskIntoConstraints = false };
                row.AddSubview(line);
                NSLayoutConstraint.ActivateConstraints(
                [
                    line.TopAnchor.ConstraintEqualTo(row.TopAnchor),
                    line.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 16),
                    line.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor)
                ]);
            }
            return row;
        }

        public void SetOpen(bool open, bool animated)
        {
            if (IsOpen == open) return;
            void Apply()
            {
                _rows.Hidden = !open;
                _rows.Alpha = open ? 1 : 0;
                _chevron.Transform = open ? CGAffineTransform.MakeRotation((nfloat)Math.PI) : CGAffineTransform.MakeIdentity();
            }
            if (animated) UIView.Animate(0.25, Apply);
            else Apply();
            UpdateAccessibility();
        }

        private void UpdateAccessibility()
        {
            if (_header is not { } header) return;
            header.AccessibilityLabel = Title;
            header.AccessibilityValue = Caption;
            header.AccessibilityTraits = IsOpen ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Header | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button | UIAccessibilityTrait.Header;
        }
    }
}
