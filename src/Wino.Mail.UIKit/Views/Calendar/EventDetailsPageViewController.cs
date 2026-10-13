using System.Collections.Specialized;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// Event details (Mac EventDetailsPageViewController, Windows EventDetailsPage), idiom-neutral. One vertical scroll with
/// the read-only notice, the action card (Join online, RSVP, Delete, Edit series; Show as and Reminder menus), the RSVP
/// panel, the title with the calendar colour, the detail rows with Wino glyphs, the people card with RSVP glyphs, the
/// HTML description (shared WebKit reader session) and the attachments. Driven by the shared
/// <see cref="EventDetailsPageViewModel"/>; the parameter is a <see cref="CalendarItemTarget"/>.
/// Hosting: pushed on a navigation controller (iPhone) the content sits in a Wino zone inside the safe area; hosted as a
/// child of the iPad reader-column frame (which draws the zone) it fills the clear root.
/// Partials: .Actions (action card, RSVP, Show as, reminders, saving), .Sections (people, description, attachments,
/// helper views), .Debug (debug bridge hooks).
/// </summary>
public sealed partial class EventDetailsPageViewController(EventDetailsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<EventDetailsPageViewModel>(viewModel, dispatcher, logger)
{
    private static readonly UIColor CardFill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));
    private const double ColumnInset = 16;

    private readonly UIView _content = new() { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear };
    private readonly WinoZoneView _zone = new();
    private readonly UIScrollView _scroll = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        AlwaysBounceVertical = true,
        KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive
    };
    private readonly UIStackView _column = WinoLayout.VStack(12);
    private readonly WinoInfoBar _readOnlyBar = new(WinoInfoBarSeverity.Warning, Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message)
    {
        IsClosable = false,
        Hidden = true
    };
    private readonly WinoSurfaceView _colorSwatch = new() { CornerRadius = 3 };
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
    private readonly EventDetailRow _when = new(WinoIconGlyph.Clock);
    private readonly EventDetailRow _location = new(WinoIconGlyph.Location);
    private readonly EventDetailRow _online = new(WinoIconGlyph.EventJoinOnline);
    private readonly EventDetailRow _repeat = new(WinoIconGlyph.CalendarEventRepeat);
    private readonly EventDetailRow _reminderRow = new(WinoIconGlyph.Reminder);
    private readonly EventDetailRow _calendar = new(WinoIconGlyph.Calendar);

    private BindingScope? _eventBindings;
    private bool _isReadOnly;
    private bool _released;
    private bool? _zoned;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        Title = Translator.CalendarEventDetails_Details;

        // Read-only calendars (subscriptions, birthdays, shared without write access) keep the event viewable but drop
        // the actions that would change it.
        AddRow(_readOnlyBar);

        // Action card and RSVP panel (.Actions).
        AddRow(BuildActionCard());
        AddRow(BuildRsvpPanel());

        // Title with the calendar colour; the swatch sits on the first title line.
        var swatchHolder = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(swatchHolder, 12, Math.Ceiling((double)_title.Font.LineHeight));
        WinoLayout.Size(_colorSwatch, 12, 12);
        swatchHolder.AddSubview(_colorSwatch);
        _colorSwatch.CenterXAnchor.ConstraintEqualTo(swatchHolder.CenterXAnchor).Active = true;
        _colorSwatch.CenterYAnchor.ConstraintEqualTo(swatchHolder.CenterYAnchor).Active = true;
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var titleRow = WinoLayout.HStack(10, swatchHolder, _title);
        titleRow.Alignment = UIStackViewAlignment.Top;
        AddRow(titleRow);

        // Detail rows; hidden rows leave the stack's layout.
        var details = WinoLayout.VStack(10, _when, _location, _online, _repeat, _reminderRow, _calendar);
        details.Alignment = UIStackViewAlignment.Fill;
        AddRow(details);

        // People, description and attachments (.Sections).
        AddRow(BuildPeopleCard());
        AddRow(BuildDescriptionCard());
        AddRow(BuildAttachmentsCard());

        WinoLayout.Insets(_column, ColumnInset, ColumnInset, ColumnInset + 8, ColumnInset);
        _column.Alignment = UIStackViewAlignment.Fill;
        _scroll.AddSubview(_column);
        NSLayoutConstraint.ActivateConstraints(
        [
            _column.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            _column.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            _column.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            _column.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            _column.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);
        WinoLayout.Fill(_scroll, _content);

        WinoTraits.OnAppearanceChanged(root, _ => AppearanceChanged());
        View = root;
        ApplyHosting(root);
    }

    private void AddRow(UIView view) => _column.AddArrangedSubview(view);

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        ApplyHosting(ViewIfLoaded);
    }

    /// <summary>
    /// Pushed on a navigation controller (iPhone Calendar tab) the content gets its own Wino zone inside the safe area;
    /// as a child of the iPad reader-column frame (which already draws the zone) it fills the clear root.
    /// </summary>
    private void ApplyHosting(UIView? root)
    {
        if (root is null) return;
        bool zoned = ParentViewController is UINavigationController;
        if (_zoned == zoned) return;
        _zoned = zoned;
        // Removing a view drops the constraints that tie it to its old superview.
        _content.RemoveFromSuperview();
        _zone.RemoveFromSuperview();
        if (zoned)
        {
            root.AddSubview(_zone);
            var guide = root.SafeAreaLayoutGuide;
            var gutter = (nfloat)WinoStyle.ZoneGutter;
            NSLayoutConstraint.ActivateConstraints(
            [
                _zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
                _zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
                _zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
                // The keyboard guide keeps the RSVP message field reachable while typing (Compose pattern).
                _zone.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor, -gutter)
            ]);
            WinoLayout.Fill(_content, _zone.ContentView);
        }
        else
        {
            WinoLayout.Fill(_content, root);
        }
    }

    /// <summary>Whether the shown event is on a read-only calendar (debug bridge and tests).</summary>
    public bool IsReadOnlyEvent => _isReadOnly;

    /// <summary>A one-line summary of the read-only presentation, for the debug bridge.</summary>
    public string DescribeReadOnlyState()
        => $"title='{ViewModel.CurrentEvent?.Title}' readOnly={_isReadOnly} bar={!_readOnlyBar.Hidden} delete={!_delete.Hidden} rsvp={!_rsvp.Hidden} showAs={_showAs.Enabled}";

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // Saving keeps the page open; Delete still navigates the shell back to the calendar.
        ViewModel.NavigatesBackAfterSave = false;
        ViewModel.CanNavigateShell = true;
        PrepareDescriptionReader();

        NotifyCollectionChangedEventHandler reminderOptionsChanged = ReminderOptionsChanged;
        ViewModel.ReminderOptions.CollectionChanged += reminderOptionsChanged;
        Bindings.Own(new ActionDisposable(() => ViewModel.ReminderOptions.CollectionChanged -= reminderOptionsChanged));

        Bind(nameof(ViewModel.CurrentEvent), vm => vm.CurrentEvent, _ => ApplyEvent());
        Bind(nameof(ViewModel.CurrentSettings), vm => vm.CurrentSettings, _ => { if (ViewModel.CurrentEvent is { } item) ApplyEventFields(item); });
        Bind(nameof(ViewModel.IsRsvpPanelVisible), vm => vm.IsRsvpPanelVisible, visible => _rsvpPanel.Hidden = !visible || _isReadOnly);
        Bind(nameof(ViewModel.RsvpMessage), vm => vm.RsvpMessage, value => { if (_rsvpMessage.Text != value) _rsvpMessage.Text = value ?? string.Empty; });
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, _ => UpdateShowAsMenu());
        Bind(nameof(ViewModel.CurrentRsvpText), vm => vm.CurrentRsvpText, _ => ApplyRsvp());
        Bind(nameof(ViewModel.CanEditSeries), vm => vm.CanEditSeries, can => _series.Hidden = !can);
        Bind(nameof(ViewModel.Reminders), vm => vm.Reminders, _ => QueueReminderMenu());
        Bind(nameof(ViewModel.HasAttachments), vm => vm.HasAttachments, _ => ApplyAttachments());

        await ViewModel.InitializeNavigationAsync(mode, parameter);
    }

    private void ApplyEvent()
    {
        var item = ViewModel.CurrentEvent;
        _eventBindings?.Dispose();
        _eventBindings = null;
        if (item is null || _released) return;
        if (!_saving) _persistedShowAs = item.CalendarItem.ShowAs;
        var scope = _eventBindings = new BindingScope();
        scope.Own(new PropertyBinding<CalendarItemViewModel, string>(item, nameof(item.Title), i => i.Title, _ => ApplyEventFields(item), Dispatcher, ReportError));
        scope.Own(new PropertyBinding<CalendarItemViewModel, bool>(item, nameof(item.IsBusy), i => i.IsBusy, busy => _content.Alpha = busy ? (nfloat)0.7 : 1, Dispatcher, ReportError));
        NotifyCollectionChangedEventHandler attendeesChanged = AttendeesChanged;
        item.Attendees.CollectionChanged += attendeesChanged;
        scope.Own(new ActionDisposable(() => item.Attendees.CollectionChanged -= attendeesChanged));
        ApplyEventFields(item);
        ApplyAttendees();
        ApplyRsvp();
    }

    private void AttendeesChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => _ = Dispatcher.ExecuteOnUIThread(() => { if (!_released) ApplyAttendees(); });

    private void ApplyEventFields(CalendarItemViewModel item)
    {
        var settings = ViewModel.CurrentSettings;
        _title.Text = item.Title ?? string.Empty;
        _colorSwatch.Fill = CalendarTileMapper.Fill(item);
        _when.Text = CalendarTileMapper.DateString(item, settings);
        _location.Text = item.Location;
        _online.Text = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out var uri) && uri is not null ? uri.Host : null;
        _repeat.Text = item.IsRecurringEvent ? CalendarTileMapper.RecurrenceString(item, settings) : null;
        var calendar = item.AssignedCalendar;
        string? accountName = calendar?.MailAccount?.Name;
        _calendar.Text = calendar is null ? null : string.IsNullOrEmpty(accountName) ? calendar.Name : $"{calendar.Name} · {accountName}";
        UpdateReminderRow();
        ApplyDescription(item.CalendarItem.Description);
        ApplyReadOnly(item);
    }

    /// <summary>Shows the read-only notice, hides Delete and RSVP and locks Show as and Reminder for read-only calendars.</summary>
    private void ApplyReadOnly(CalendarItemViewModel item)
    {
        _isReadOnly = CalendarTileMapper.IsReadOnly(item);
        _readOnlyBar.Hidden = !_isReadOnly;
        _delete.Hidden = _isReadOnly;
        _rsvp.Hidden = _isReadOnly;
        _showAs.Enabled = !_isReadOnly;
        _reminder.Enabled = !_isReadOnly;
        _rsvpPanel.Hidden = _isReadOnly || !ViewModel.IsRsvpPanelVisible;
    }

    private bool IsDarkAppearance() => WinoTraits.IsDark(ViewIfLoaded?.TraitCollection ?? TraitCollection);

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override async Task DeactivateAsync()
    {
        _released = true;
        _eventBindings?.Dispose();
        _eventBindings = null;
        _attachmentBindings?.Dispose();
        _attachmentBindings = null;
        await base.DeactivateAsync();
        await DisposeDescriptionReaderAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            _eventBindings?.Dispose();
            _eventBindings = null;
            _attachmentBindings?.Dispose();
            _attachmentBindings = null;
            if (!_readerDisposed && _descriptionReader is not null) _ = DisposeDescriptionReaderAsync();
        }
        base.Dispose(disposing);
    }
}
