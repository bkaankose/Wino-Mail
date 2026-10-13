using CoreGraphics;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The Windows quick event popup over an empty slot (Mac QuickEventPopover): calendar header in the calendar colour,
/// event name, all-day switch, start and end time, location, then Save and "More details". Everything binds to the
/// CalendarPageViewModel quick event state. iPad presents it as a popover anchored to the slot, iPhone as a sheet with
/// the medium detent (the slot is long-pressed).
/// </summary>
public sealed class QuickEventViewController : UIViewController
{
    private const double PreferredWidth = 340;

    private readonly CalendarPageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly BindingScope _bindings = new();
    private readonly UITextField _name = new() { TranslatesAutoresizingMaskIntoConstraints = false, BorderStyle = UITextBorderStyle.RoundedRect };
    private readonly UISwitch _allDay = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _start = MenuButton();
    private readonly UIButton _end = MenuButton();
    private readonly UITextField _location = new() { TranslatesAutoresizingMaskIntoConstraints = false, BorderStyle = UITextBorderStyle.RoundedRect };
    private readonly UILabel _range = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
    private readonly WinoSurfaceView _header = new() { CornerRadius = 0 };
    private readonly UILabel _calendarName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly UILabel _accountName = WinoStyle.Label(string.Empty, WinoStyle.Caption);
    private UIStackView? _timeRow;
    private UIStackView? _readOnlyNote;
    private UIButton? _save;
    private CommandBinding? _saveBinding;
    private PresentationDelegate? _presentationDelegate;
    private bool _closedByUs;
    private bool _closed;

    public QuickEventViewController(CalendarPageViewModel viewModel, IDispatcher dispatcher, Action<Exception> error)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _error = error;
    }

    /// <summary>Raised once when the quick event closes for any reason.</summary>
    public event EventHandler? Closed;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemBackground };

        var headerRow = WinoLayout.Insets(WinoLayout.HStack(10, _accountName, _calendarName, WinoLayout.Spacer()), 0, 14, 0, 14);
        headerRow.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(headerRow, _header);
        _header.HeightAnchor.ConstraintEqualTo(40).Active = true;

        _name.Placeholder = Translator.QuickEventDialog_EventName;
        _name.Font = WinoStyle.Scaled(17);
        _name.ReturnKeyType = UIReturnKeyType.Done;
        _name.EditingChanged += (_, _) => _viewModel.EventName = _name.Text ?? string.Empty;
        _name.ShouldReturn = field => { field.ResignFirstResponder(); return true; };
        _name.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _allDay.ValueChanged += (_, _) => _viewModel.IsAllDay = _allDay.On;
        var allDayLabel = WinoStyle.Label(Translator.QuickEventDialog_IsAllDay, WinoStyle.Subheadline, WinoStyle.SecondaryText);
        var nameRow = WinoLayout.HStack(10, _name, allDayLabel, _allDay);
        nameRow.Alignment = UIStackViewAlignment.Center;

        var dash = WinoStyle.Label("–", WinoStyle.Body, WinoStyle.SecondaryText);
        _timeRow = WinoLayout.HStack(8, _start, dash, _end, WinoLayout.Spacer());
        _timeRow.Alignment = UIStackViewAlignment.Center;
        RebuildTimeMenus();

        _location.Placeholder = Translator.QuickEventDialog_Location;
        _location.EditingChanged += (_, _) => _viewModel.Location = _location.Text ?? string.Empty;
        _location.ShouldReturn = field => { field.ResignFirstResponder(); return true; };
        var locationRow = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.Location, 18, WinoStyle.SecondaryText), _location);
        locationRow.Alignment = UIStackViewAlignment.Center;

        var save = SettingsBinderButton(Translator.Buttons_Save, primary: true);
        _save = save;
        _saveBinding = _bindings.Own(new CommandBinding(_viewModel.SaveQuickEventCommand, () => null, enabled => save.Enabled = enabled, _dispatcher, _error));
        save.PrimaryActionTriggered += (_, _) => Save();
        var more = SettingsBinderButton(Translator.QuickEventDialogMoreDetailsButtonText, primary: false);
        more.PrimaryActionTriggered += (_, _) => MoreDetails();
        var buttons = WinoLayout.HStack(8, WinoLayout.Spacer(), more, save);

        // A read-only calendar cannot take the event (CanSaveQuickEvent is false); say why Save is off.
        var readOnlyText = WinoStyle.Label($"{Translator.CalendarReadOnly_Title}. {Translator.CalendarReadOnly_Message}", WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        _readOnlyNote = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.Warning, 14, WinoStyle.Caution), readOnlyText);
        _readOnlyNote.Alignment = UIStackViewAlignment.Top;
        _readOnlyNote.Hidden = true;

        var rows = WinoLayout.VStack(12, _readOnlyNote, nameRow, _timeRow, _range, locationRow, buttons);
        rows.Alignment = UIStackViewAlignment.Fill;
        _location.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var form = WinoLayout.Insets(rows, 14, 14, 14, 14);
        var column = WinoLayout.VStack(0, _header, form);
        column.Alignment = UIStackViewAlignment.Fill;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(guide.BottomAnchor)
        ]);
        View = root;
        Bind();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (View is not { } view || ModalPresentationStyle != UIModalPresentationStyle.Popover) return;
        var size = view.SystemLayoutSizeFittingSize(new CGSize(PreferredWidth, 0), 1000, 1);
        var preferred = new CGSize(PreferredWidth, Math.Ceiling(size.Height));
        if (PreferredContentSize != preferred) PreferredContentSize = preferred;
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (string.IsNullOrEmpty(_name.Text)) _name.BecomeFirstResponder();
    }

    private void Bind()
    {
        Own(nameof(_viewModel.EventName), vm => vm.EventName, value => { if (_name.Text != value) _name.Text = value ?? string.Empty; });
        Own(nameof(_viewModel.Location), vm => vm.Location, value => { if (_location.Text != value) _location.Text = value ?? string.Empty; });
        Own(nameof(_viewModel.IsAllDay), vm => vm.IsAllDay, value => { _allDay.On = value; if (_timeRow is { } row) row.Hidden = value; });
        Own(nameof(_viewModel.SelectedStartTimeString), vm => vm.SelectedStartTimeString, value => SetTitle(_start, value));
        Own(nameof(_viewModel.SelectedEndTimeString), vm => vm.SelectedEndTimeString, value => SetTitle(_end, value));
        Own(nameof(_viewModel.HourSelectionStrings), vm => vm.HourSelectionStrings, _ => RebuildTimeMenus());
        Own(nameof(_viewModel.QuickEventDateRangeText), vm => vm.QuickEventDateRangeText, value => _range.Text = value ?? string.Empty);
        Own(nameof(_viewModel.SelectedQuickEventAccountCalendar), vm => vm.SelectedQuickEventAccountCalendar, _ => ApplyCalendar());
        Own(nameof(_viewModel.SelectedQuickEventDate), vm => vm.SelectedQuickEventDate, value => { if (value is null) Dismiss(); });
    }

    private void Own<TValue>(string property, Func<CalendarPageViewModel, TValue> read, Action<TValue> apply)
        => _bindings.Own(new PropertyBinding<CalendarPageViewModel, TValue>(_viewModel, property, read, apply, _dispatcher, _error));

    private void RebuildTimeMenus()
    {
        var hours = _viewModel.HourSelectionStrings ?? [];
        _start.Menu = TimeMenu(hours, value => _viewModel.SelectedStartTimeString = value);
        _end.Menu = TimeMenu(hours, value => _viewModel.SelectedEndTimeString = value);
    }

    private static UIMenu TimeMenu(IReadOnlyList<string> hours, Action<string> choose)
    {
        var actions = new List<UIMenuElement>();
        foreach (var hour in hours) actions.Add(UIAction.Create(hour, null, null, _ => choose(hour)));
        return UIMenu.Create(actions.ToArray());
    }

    private static void SetTitle(UIButton button, string? value)
    {
        var configuration = button.Configuration ?? UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = string.IsNullOrEmpty(value) ? "--:--" : value;
        button.Configuration = configuration;
    }

    private static UIButton MenuButton()
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = "--:--";
        return new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
    }

    private static UIButton SettingsBinderButton(string title, bool primary)
    {
        var configuration = primary ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title;
        return new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
    }

    private void ApplyCalendar()
    {
        var calendar = _viewModel.SelectedQuickEventAccountCalendar;
        var fill = WinoStyle.FromHexString(calendar?.BackgroundColorHex) ?? UIColor.SystemGray4;
        _header.Fill = fill;
        var text = WinoCalendarItemView.ReadableTextColor(fill, TraitCollection);
        _accountName.Text = calendar?.Account?.Name ?? string.Empty;
        _accountName.TextColor = text.ColorWithAlpha(0.85f);
        _calendarName.Text = _viewModel.SelectedQuickEventAccountCalendarName ?? string.Empty;
        _calendarName.TextColor = text;
        if (_readOnlyNote is { } note) note.Hidden = calendar?.IsReadOnly != true;
    }

    private void Save()
    {
        _closedByUs = true;
        _saveBinding?.Execute();
        Dismiss();
        _viewModel.SelectedQuickEventDate = null;
    }

    private void MoreDetails()
    {
        _closedByUs = true;
        // The composer reads the quick event state, so it opens before the selection clears.
        _viewModel.GoToEventComposePageCommand.Execute(null);
        Dismiss();
        _viewModel.SelectedQuickEventDate = null;
    }

#if DEBUG
    /// <summary>Debug bridge: types a name and saves like the Save button.</summary>
    public string DebugSave(string name)
    {
        _viewModel.EventName = name;
        bool enabled = _save?.Enabled == true;
        Save();
        return enabled ? "saved " + name : "save disabled";
    }
#endif

    /// <summary>
    /// Presents the quick event from <paramref name="presenter"/>: a popover anchored to <paramref name="anchorRect"/> in
    /// <paramref name="anchorView"/>, or a sheet with the medium detent when <paramref name="anchorView"/> is null.
    /// </summary>
    public void Present(UIViewController presenter, UIView? anchorView, CGRect anchorRect)
    {
        if (anchorView is not null)
        {
            ModalPresentationStyle = UIModalPresentationStyle.Popover;
            PreferredContentSize = new CGSize(PreferredWidth, 300);
            if (PopoverPresentationController is { } popover)
            {
                popover.SourceView = anchorView;
                popover.SourceRect = anchorRect;
                popover.PermittedArrowDirections = UIPopoverArrowDirection.Left | UIPopoverArrowDirection.Right | UIPopoverArrowDirection.Up | UIPopoverArrowDirection.Down;
            }
        }
        else
        {
            ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
            if (SheetPresentationController is { } sheet)
            {
                sheet.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent(), UISheetPresentationControllerDetent.CreateLargeDetent()];
                sheet.PrefersGrabberVisible = true;
            }
        }
        _presentationDelegate = new PresentationDelegate(this);
        if (PresentationController is { } presentation) presentation.Delegate = _presentationDelegate;
        presenter.PresentViewController(this, true, null);
    }

    /// <summary>Closes the quick event without saving (another tap on the surface, the page leaving).</summary>
    public void Dismiss()
    {
        _closedByUs = true;
        if (PresentingViewController is { } presenting && !IsBeingDismissed) presenting.DismissViewController(true, HandleClosed);
        else HandleClosed();
    }

    private void HandleClosed()
    {
        if (_closed) return;
        _closed = true;
        if (!_closedByUs) _viewModel.SelectedQuickEventDate = null;
        _bindings.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class PresentationDelegate(QuickEventViewController owner) : UIPopoverPresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => owner.HandleClosed();

        // Keep the popover a popover on iPad in compact width splits; iPhone presents a sheet anyway.
        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController controller, UITraitCollection traitCollection)
            => owner.ModalPresentationStyle == UIModalPresentationStyle.Popover ? UIModalPresentationStyle.None : owner.ModalPresentationStyle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}
