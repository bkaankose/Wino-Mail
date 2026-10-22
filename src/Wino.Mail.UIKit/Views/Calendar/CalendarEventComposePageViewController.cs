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
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The event composer (Mac CalendarEventComposePageViewController, Windows CalendarEventComposePage) for iPhone and
/// iPad. The host shows it as the root of a navigation controller: Cancel on the left, Save / Send on the right. Inside
/// one Wino zone: the title, the Mac command-bar pickers (calendar with its colour, show as, reminder, private, online
/// meeting), when (dates, all day, time zone), repeat with the custom editor, location, rich-text notes (the mail
/// composer's WKWebView editor with its Format / Insert / Options bar above the keyboard), attendees with contact
/// suggestions and attachments. Narrower than 700pt everything is one vertical scroll; wider, attendees and attachments
/// move into the Mac's 340pt side pane. Partials: Attendees, Recurrence, Drop and Debug.
/// </summary>
public sealed partial class CalendarEventComposePageViewController(CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    IExternalLauncher launcher, IPreferencesService preferences, IServiceProvider services)
    : WinoViewController<CalendarEventComposePageViewModel>(viewModel, dispatcher, logger)
{
    private const double WideLayoutWidth = 700;
    private const double SidePaneWidth = 340;
    private const double NotesHeight = 300;
    private static readonly UIColor PaneFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.03), WinoStyle.Hex(0xFFFFFF, 0.04));

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

    private readonly IExternalLauncher _launcher = launcher;
    private readonly IPreferencesService _preferences = preferences;
    private readonly ITranslationService? _translations = services.GetService(typeof(ITranslationService)) as ITranslationService;
    private readonly IPictureStorageService? _pictures = services.GetService(typeof(IPictureStorageService)) as IPictureStorageService;
    private readonly IMailDialogService? _dialogs = services.GetService(typeof(IMailDialogService)) as IMailDialogService;

    // ---- Layout ----
    private readonly WinoZoneView _zone = new();
    private readonly UIStackView _columns = WinoLayout.HStack(0);
    private readonly UIScrollView _formScroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };
    private readonly UIStackView _formStack = WinoLayout.VStack(20);
    private readonly WinoSeparator _sideRule = new(true);
    private readonly WinoSurfaceView _sidePane = new() { Fill = PaneFill };
    private readonly UIScrollView _sideScroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };
    private readonly UIView _sideDocument = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIStackView _sideContent = WinoLayout.VStack(12);
    private bool? _wide;

    // ---- Navigation bar ----
    private UIBarButtonItem? _cancelItem;
    private UIBarButtonItem? _saveItem;
    private CommandBinding? _saveBinding;

    // ---- Title and the Mac command-bar pickers ----
    private readonly UITextField _title = new()
    {
        Font = WinoStyle.Scaled(24, UIFontWeight.Semibold),
        AdjustsFontForContentSizeCategory = true,
        BorderStyle = UITextBorderStyle.None,
        ReturnKeyType = UIReturnKeyType.Next,
        AutocapitalizationType = UITextAutocapitalizationType.Sentences,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIButton _calendarButton = MenuButton(Translator.CalendarEventCompose_SelectCalendar);
    private readonly UIButton _showAsButton = MenuButton(Translator.CalendarEventDetails_ShowAs);
    private readonly UIButton _reminderButton = MenuButton(Translator.CalendarEventDetails_Reminder);
    private readonly UISwitch _privateSwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISwitch _onlineSwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _onlineLabel = WinoStyle.Label(string.Empty, WinoStyle.Body, null, 2);
    private UIView? _onlineRow;

    // ---- When ----
    private readonly UIDatePicker _startDate = DatePicker(UIDatePickerMode.Date, Translator.CalendarEventCompose_Starts);
    private readonly UIDatePicker _startTime = DatePicker(UIDatePickerMode.Time, Translator.CalendarEventCompose_Starts);
    private readonly UIDatePicker _endDate = DatePicker(UIDatePickerMode.Date, Translator.CalendarEventCompose_Ends);
    private readonly UIDatePicker _endTime = DatePicker(UIDatePickerMode.Time, Translator.CalendarEventCompose_Ends);
    private readonly UISwitch _allDaySwitch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _duration = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 2);
    private readonly UILabel _rangeError = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.Critical, 2);
    private readonly UIButton _timeZoneButton = MenuButton(Translator.CalendarEventCompose_TimeZone);
    private readonly UILabel _localTimeHint = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 3);
    private UIView? _timeZoneRow;
    private bool _applying;

    // ---- Repeat and location ----
    private readonly UIButton _repeatButton = MenuButton(Translator.CalendarEventCompose_Repeat);
    private readonly UILabel _recurrenceSummary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 3);
    private readonly UITextField _location = new()
    {
        Font = WinoStyle.Body,
        AdjustsFontForContentSizeCategory = true,
        BorderStyle = UITextBorderStyle.RoundedRect,
        ReturnKeyType = UIReturnKeyType.Next,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    // ---- Notes (Windows WinoMailEditor): untouched notes are saved exactly as they arrived ----
    private readonly ComposeEditorWebView _notesWebView = new();
    private readonly UIView _notesHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoSurfaceView _notesCard = new() { Stroke = WinoStyle.GroupStroke, CornerRadius = 6 };
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

    // ---- Side pane: attendees and attachments ----
    private readonly UILabel _attendeesTitle = WinoStyle.Label(Translator.CalendarEventCompose_Attendees, WinoStyle.BodyStrong);
    private readonly ComposeRecipientField _invite = new(string.Empty);
    private readonly UIStackView _attendeeList = WinoLayout.VStack(2);
    private readonly UILabel _attachmentsTitle = WinoStyle.Label(Translator.CalendarEventDetails_Attachments, WinoStyle.BodyStrong);
    private readonly UIButton _addAttachment = SettingsButton(Translator.CalendarEventCompose_AddAttachment);
    private readonly UILabel _attachmentsDisabled = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 3);
    private readonly UIStackView _attachmentList = WinoLayout.VStack(2);

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
        var root = new UIView { BackgroundColor = UIColor.Clear };

        // ---- Title ----
        _title.Placeholder = Translator.CalendarEventCompose_TitlePlaceholder;
        _title.AccessibilityLabel = Translator.CalendarEventCompose_TitlePlaceholder;
        _title.EditingChanged += (_, _) =>
        {
            // Committed text only: an input method's marked text is still being composed.
            if (_title.MarkedTextRange is not null) return;
            ViewModel.Title = _title.Text ?? string.Empty;
        };
        _title.ShouldReturn = _ => { _location.BecomeFirstResponder(); return false; };
        _title.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var titleBlock = WinoLayout.VStack(6, _title, new WinoSeparator());
        titleBlock.Alignment = UIStackViewAlignment.Fill;

        // ---- The Mac command bar as form rows ----
        _calendarButton.Menu = DeferredMenu(BuildCalendarMenu);
        _showAsButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.ShowAsOptions, ViewModel.SelectedShowAsOption, option => option.DisplayText, option => ViewModel.SelectedShowAsOption = option));
        _reminderButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.ReminderOptions, ViewModel.SelectedReminderOption, option => option.DisplayText, option => ViewModel.SelectedReminderOption = option));
        _privateSwitch.ValueChanged += (_, _) => ViewModel.IsPrivate = _privateSwitch.On;
        WinoAccessibility.Label(_privateSwitch, Translator.CalendarEventCompose_Private);
        WinoAccessibility.Help(_privateSwitch, Translator.CalendarEventCompose_PrivateTooltip);
        _onlineSwitch.ValueChanged += (_, _) => ViewModel.IsOnlineMeeting = _onlineSwitch.On;
        var privateRow = Row(WinoIconGlyph.LockClosed, SwitchRow(WinoStyle.Label(Translator.CalendarEventCompose_Private, WinoStyle.Body), _privateSwitch));
        _onlineRow = Row(WinoIconGlyph.EventJoinOnline, SwitchRow(_onlineLabel, _onlineSwitch));
        var optionsBlock = WinoLayout.VStack(8,
            Row(WinoIconGlyph.Calendar, Leading(_calendarButton)),
            Row(WinoIconGlyph.CalendarShowAs, Leading(_showAsButton)),
            Row(WinoIconGlyph.Reminder, Leading(_reminderButton)),
            privateRow,
            _onlineRow);
        optionsBlock.Alignment = UIStackViewAlignment.Fill;

        // ---- When ----
        foreach (var picker in new[] { _startDate, _startTime, _endDate, _endTime }) picker.ValueChanged += (_, _) => PushDates();
        _allDaySwitch.ValueChanged += (_, _) => ViewModel.IsAllDay = _allDaySwitch.On;
        WinoAccessibility.Label(_allDaySwitch, Translator.CalendarEventCompose_AllDay);
        var startsRow = Leading(WinoLayout.HStack(8, FieldLabel(Translator.CalendarEventCompose_Starts), _startDate, _startTime));
        var endsRow = Leading(WinoLayout.HStack(8, FieldLabel(Translator.CalendarEventCompose_Ends), _endDate, _endTime));
        var allDayRow = SwitchRow(WinoStyle.Label(Translator.CalendarEventCompose_AllDay, WinoStyle.Body), _allDaySwitch);
        _timeZoneButton.Menu = DeferredMenu(BuildTimeZoneMenu);
        _timeZoneRow = Leading(WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.Globe, 14, WinoStyle.SecondaryText), _timeZoneButton));
        _localTimeHint.Hidden = true;
        _rangeError.Hidden = true;
        var whenColumn = WinoLayout.VStack(8, startsRow, endsRow, _duration, _rangeError, allDayRow, _timeZoneRow, _localTimeHint);
        whenColumn.Alignment = UIStackViewAlignment.Fill;
        var whenRow = Row(WinoIconGlyph.Clock, whenColumn);

        // ---- Repeat ----
        _repeatButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.RepeatOptions, ViewModel.SelectedRepeatOption, option => option.DisplayText, option => ViewModel.SelectedRepeatOption = option));
        var repeatColumn = WinoLayout.VStack(8, Leading(_repeatButton), BuildCustomRecurrence(), BuildRecurrenceEnd(), _recurrenceSummary);
        repeatColumn.Alignment = UIStackViewAlignment.Fill;
        var repeatRow = Row(WinoIconGlyph.CalendarEventRepeat, repeatColumn);

        // ---- Location ----
        _location.Placeholder = Translator.CalendarEventCompose_LocationPlaceholder;
        _location.AccessibilityLabel = Translator.CalendarEventCompose_LocationPlaceholder;
        _location.EditingChanged += (_, _) =>
        {
            if (_location.MarkedTextRange is not null) return;
            ViewModel.Location = _location.Text ?? string.Empty;
        };
        _location.ShouldReturn = _ => { FocusNotes(); return false; };
        _location.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var locationRow = Row(WinoIconGlyph.Location, _location);

        // ---- Notes ----
        _notesWebView.AccessibilityLabel = Translator.CalendarEventCompose_Notes;
        WinoLayout.Fill(_notesWebView, _notesCard, 4, 6, 4, 6);
        WinoLayout.Fill(_notesCard, _notesHost);
        _notesHost.AddSubview(_notesSpinner);
        NSLayoutConstraint.ActivateConstraints(
        [
            _notesHost.HeightAnchor.ConstraintEqualTo((nfloat)NotesHeight),
            _notesSpinner.CenterXAnchor.ConstraintEqualTo(_notesHost.CenterXAnchor),
            _notesSpinner.CenterYAnchor.ConstraintEqualTo(_notesHost.CenterYAnchor)
        ]);
        var notesLabel = WinoStyle.Label(Translator.CalendarEventCompose_Notes, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        var notesColumn = WinoLayout.VStack(6, notesLabel, _notesHost);
        notesColumn.Alignment = UIStackViewAlignment.Fill;
        var notesRow = Row(WinoIconGlyph.Note, notesColumn);

        _formStack.Alignment = UIStackViewAlignment.Fill;
        foreach (var view in new UIView[] { titleBlock, optionsBlock, whenRow, repeatRow, locationRow, notesRow })
            _formStack.AddArrangedSubview(view);
        _formStack.TranslatesAutoresizingMaskIntoConstraints = false;
        _formScroll.AddSubview(_formStack);
        NSLayoutConstraint.ActivateConstraints(
        [
            _formStack.LeadingAnchor.ConstraintEqualTo(_formScroll.ContentLayoutGuide.LeadingAnchor),
            _formStack.TrailingAnchor.ConstraintEqualTo(_formScroll.ContentLayoutGuide.TrailingAnchor),
            _formStack.TopAnchor.ConstraintEqualTo(_formScroll.ContentLayoutGuide.TopAnchor),
            _formStack.BottomAnchor.ConstraintEqualTo(_formScroll.ContentLayoutGuide.BottomAnchor),
            _formStack.WidthAnchor.ConstraintEqualTo(_formScroll.FrameLayoutGuide.WidthAnchor)
        ]);

        // ---- Side content (attendees, attachments) ----
        var attendeesHeader = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.CalendarAttendees, 16, WinoStyle.SecondaryText), _attendeesTitle, WinoLayout.Spacer());
        ConfigureInviteField();
        _attendeeList.Alignment = UIStackViewAlignment.Fill;
        _attachmentList.Alignment = UIStackViewAlignment.Fill;
        var addBinding = Bindings.Own(new CommandBinding(ViewModel.AddAttachmentsCommand, () => null, enabled => _addAttachment.Enabled = enabled && ViewModel.CanAddAttachments, Dispatcher, ReportError));
        _addAttachment.PrimaryActionTriggered += (_, _) => addBinding.Execute();
        var attachmentsHeader = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText), _attachmentsTitle, WinoLayout.Spacer(), _addAttachment);
        _attachmentsDisabled.Hidden = true;
        _sideContent.Alignment = UIStackViewAlignment.Fill;
        foreach (var view in new UIView[] { attendeesHeader, _invite, _attendeeList, Gap(12), attachmentsHeader, _attachmentsDisabled, _attachmentList })
            _sideContent.AddArrangedSubview(view);

        _sideScroll.AddSubview(_sideDocument);
        NSLayoutConstraint.ActivateConstraints(
        [
            _sideDocument.LeadingAnchor.ConstraintEqualTo(_sideScroll.ContentLayoutGuide.LeadingAnchor),
            _sideDocument.TrailingAnchor.ConstraintEqualTo(_sideScroll.ContentLayoutGuide.TrailingAnchor),
            _sideDocument.TopAnchor.ConstraintEqualTo(_sideScroll.ContentLayoutGuide.TopAnchor),
            _sideDocument.BottomAnchor.ConstraintEqualTo(_sideScroll.ContentLayoutGuide.BottomAnchor),
            _sideDocument.WidthAnchor.ConstraintEqualTo(_sideScroll.FrameLayoutGuide.WidthAnchor)
        ]);
        WinoLayout.Fill(_sideScroll, _sidePane);
        _sidePane.WidthAnchor.ConstraintEqualTo((nfloat)SidePaneWidth).Active = true;

        // ---- Columns in the zone ----
        _columns.Alignment = UIStackViewAlignment.Fill;
        _columns.AddArrangedSubview(_formScroll);
        _columns.AddArrangedSubview(_sideRule);
        _columns.AddArrangedSubview(_sidePane);
        _formScroll.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _formScroll.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Horizontal);
        WinoLayout.Fill(_columns, _zone.ContentView);
        BuildInviteSuggestions(_zone.ContentView);

        root.AddSubview(_zone);
        var guide = root.SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            _zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            _zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            _zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            // The keyboard guide includes the formatting bar, so the form ends above it while editing.
            _zone.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor, -gutter)
        ]);
        View = root;

        ApplyLayout(false);
        BuildEditorToolbar();
        BuildNavigationItems();
        SetUpAttachmentDrop();
        WinoTraits.OnAppearanceChanged(root, _ => FollowAppearance());
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        ApplyLayout(View is { } view && view.Bounds.Width >= WideLayoutWidth);
        PositionInviteSuggestions();
    }

    /// <summary>
    /// One scroll below 700pt (attendees and attachments after the notes); the Mac two-column layout from 700pt, with
    /// attendees and attachments in the 340pt side pane.
    /// </summary>
    private void ApplyLayout(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        CloseInviteSuggestions();
        if (_sideContent.Superview is not null)
        {
            if (_sideContent.Superview is UIStackView stack) stack.RemoveArrangedSubview(_sideContent);
            _sideContent.RemoveFromSuperview();
        }
        if (wide)
        {
            WinoLayout.Insets(_formStack, 24, 32, 32, 32);
            WinoLayout.Fill(_sideContent, _sideDocument, 20, 20, 20, 20);
        }
        else
        {
            WinoLayout.Insets(_formStack, 16, 16, 24, 16);
            _formStack.AddArrangedSubview(_sideContent);
        }
        _sideRule.Hidden = !wide;
        _sidePane.Hidden = !wide;
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
            _startTime.Hidden = value;
            _endTime.Hidden = value;
            if (_timeZoneRow is { } zone) zone.Hidden = value;
        });
        Bind(nameof(ViewModel.SelectedTimeZoneOption), vm => vm.SelectedTimeZoneOption, option => SetMenuTitle(_timeZoneButton, option?.DisplayText));
        Bind(nameof(ViewModel.LocalTimeHintText), vm => vm.LocalTimeHintText, text => { _localTimeHint.Text = text ?? string.Empty; _localTimeHint.Hidden = string.IsNullOrEmpty(text); });
        Bind(nameof(ViewModel.IsPrivate), vm => vm.IsPrivate, value => _privateSwitch.On = value);
        Bind(nameof(ViewModel.IsOnlineMeeting), vm => vm.IsOnlineMeeting, value => _onlineSwitch.On = value);
        Bind(nameof(ViewModel.StartDate), vm => vm.StartDate, _ => { PullDates(); ApplyRepeatTitle(); });
        Bind(nameof(ViewModel.StartTime), vm => vm.StartTime, _ => PullDates());
        Bind(nameof(ViewModel.EndDate), vm => vm.EndDate, _ => PullDates());
        Bind(nameof(ViewModel.EndTime), vm => vm.EndTime, _ => PullDates());
        Bind(nameof(ViewModel.DurationText), vm => vm.DurationText, _ => ApplyDuration());
        Bind(nameof(ViewModel.IsDateRangeValid), vm => vm.IsDateRangeValid, _ => ApplyDuration());
        Bind(nameof(ViewModel.RecurrenceSummary), vm => vm.RecurrenceSummary, value => { _recurrenceSummary.Text = value ?? string.Empty; _recurrenceSummary.Hidden = string.IsNullOrEmpty(value); });
        Bind(nameof(ViewModel.SelectedRepeatOption), vm => vm.SelectedRepeatOption, _ => ApplyRepeatTitle());
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, option => SetMenuTitle(_showAsButton, option?.DisplayText));
        Bind(nameof(ViewModel.SelectedReminderOption), vm => vm.SelectedReminderOption, option => SetMenuTitle(_reminderButton, option?.DisplayText));
        Bind(nameof(ViewModel.SelectedCalendar), vm => vm.SelectedCalendar, _ => { ApplyCalendar(); ApplyAttendees(); });
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
            _title.BecomeFirstResponder();
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
        _notesCard.Fill = WinoStyle.SubtleFill;
        WinoLayout.Fill(text, _notesCard, 4, 6, 4, 6);
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
        SetMenuTitle(_calendarButton, selected?.Name ?? Translator.CalendarEventCompose_SelectCalendar, selected is null ? null : ColorDot(CalendarColor(selected)));
        _calendarButton.AccessibilityValue = selected is null ? null : $"{selected.Name}, {selected.Account?.Address}";
        _onlineLabel.Text = string.IsNullOrEmpty(ViewModel.OnlineMeetingProviderText) ? Translator.CalendarEventCompose_OnlineMeetingInfoTitle : ViewModel.OnlineMeetingProviderText;
        WinoAccessibility.Label(_onlineSwitch, _onlineLabel.Text);
        if (_onlineRow is { } online) online.Hidden = !ViewModel.CanAddOnlineMeeting;
        var canAttach = ViewModel.CanAddAttachments;
        _addAttachment.Enabled = canAttach && ViewModel.AddAttachmentsCommand.CanExecute(null);
        _attachmentsDisabled.Text = canAttach ? string.Empty : ViewModel.AttachmentsDisabledTooltipText ?? string.Empty;
        _attachmentsDisabled.Hidden = canAttach || string.IsNullOrEmpty(_attachmentsDisabled.Text);
    }

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
    }

    private void PullDates()
    {
        _applying = true;
        try
        {
            _startDate.Date = ToNSDate(ViewModel.StartDate.Date);
            _startTime.Date = ToNSDate(ViewModel.StartDate.Date + ViewModel.StartTime);
            _endDate.Date = ToNSDate(ViewModel.EndDate.Date);
            _endTime.Date = ToNSDate(ViewModel.EndDate.Date + ViewModel.EndTime);
        }
        finally { _applying = false; }
    }

    private void PushDates()
    {
        if (_applying) return;
        var startDate = FromNSDate(_startDate.Date).Date;
        var endDate = FromNSDate(_endDate.Date).Date;
        var startTime = FromNSDate(_startTime.Date).TimeOfDay;
        var endTime = FromNSDate(_endTime.Date).TimeOfDay;
        if (ViewModel.StartDate.Date != startDate) ViewModel.StartDate = new DateTimeOffset(startDate, TimeZoneInfo.Local.GetUtcOffset(startDate));
        if (ViewModel.StartTime != startTime) ViewModel.StartTime = startTime;
        if (ViewModel.EndDate.Date != endDate) ViewModel.EndDate = new DateTimeOffset(endDate, TimeZoneInfo.Local.GetUtcOffset(endDate));
        if (ViewModel.EndTime != endTime) ViewModel.EndTime = endTime;
    }

    private static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static NSDate ToNSDate(DateTime local) => NSDate.FromTimeIntervalSinceReferenceDate((DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime() - Reference).TotalSeconds);
    private static DateTime FromNSDate(NSDate date) => Reference.AddSeconds(date.SecondsSinceReferenceDate).ToLocalTime();

    // ---- Attendees and attachments lists ----

    private void ApplyAttendees()
    {
        Clear(_attendeeList);
        _attendeesTitle.Text = ViewModel.HasAttendees ? $"{Translator.CalendarEventCompose_Attendees} {ViewModel.AttendeeCount}" : Translator.CalendarEventCompose_Attendees;
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
        var picture = new WinoContactPicture(28);
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
        WinoLayout.Insets(row, 4, 6, 4, 0);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return row;
    }

    private void ApplyAttachments()
    {
        Clear(_attachmentList);
        _attachmentsTitle.Text = ViewModel.HasAttachments ? $"{Translator.CalendarEventDetails_Attachments} {ViewModel.AttachmentCount}" : Translator.CalendarEventDetails_Attachments;
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
            WinoLayout.Insets(row, 4, 6, 4, 0);
            row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            _attachmentList.AddArrangedSubview(row);
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

    /// <summary>A grey pull-down button whose menu is its primary action (the Mac NSPopUpButton).</summary>
    private static UIButton MenuButton(string accessibilityLabel)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.ImagePadding = 6;
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = accessibilityLabel };
        button.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        return button;
    }

    private static void SetMenuTitle(UIButton button, string? title, UIImage? image = null)
    {
        var configuration = button.Configuration ?? UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title ?? string.Empty;
        configuration.Image = image;
        button.Configuration = configuration;
        button.AccessibilityValue = title;
    }

    private static UIButton SettingsButton(string title)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        return button;
    }

    private static UIDatePicker DatePicker(UIDatePickerMode mode, string accessibilityLabel)
    {
        var picker = new UIDatePicker { Mode = mode, PreferredDatePickerStyle = UIDatePickerStyle.Compact, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(picker, accessibilityLabel);
        picker.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        return picker;
    }

    private static UILabel FieldLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.WidthAnchor.ConstraintEqualTo(48).Active = true;
        label.IsAccessibilityElement = false;
        return label;
    }

    /// <summary>The view at the start of a full-width row, the rest left empty.</summary>
    private static UIStackView Leading(UIView view)
    {
        var row = WinoLayout.HStack(0, view, WinoLayout.Spacer());
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>The label on the leading side and a switch on the trailing side, at least one touch target tall.</summary>
    private static UIStackView SwitchRow(UILabel label, UISwitch control)
    {
        label.IsAccessibilityElement = false;
        label.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        label.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        control.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(12, label, control);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return row;
    }

    /// <summary>A form row: the section glyph beside its content (Mac Row).</summary>
    private static UIStackView Row(WinoIconGlyph glyph, UIView content)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText) { TranslatesAutoresizingMaskIntoConstraints = false };
        icon.IsAccessibilityElement = false;
        var iconHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        iconHost.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            iconHost.WidthAnchor.ConstraintEqualTo(20),
            iconHost.HeightAnchor.ConstraintEqualTo((nfloat)WinoStyle.MinimumHitTarget),
            icon.WidthAnchor.ConstraintEqualTo(16),
            icon.HeightAnchor.ConstraintEqualTo(16),
            icon.CenterXAnchor.ConstraintEqualTo(iconHost.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(iconHost.CenterYAnchor)
        ]);
        content.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(12, iconHost, content);
        row.Alignment = UIStackViewAlignment.Top;
        return row;
    }

    private static UIView Gap(double height) => WinoLayout.Size(new UIView { TranslatesAutoresizingMaskIntoConstraints = false }, -1, height);
}
