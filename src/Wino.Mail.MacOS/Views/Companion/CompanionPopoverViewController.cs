using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.ViewModels.Companion;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Companion;

/// <summary>
/// The companion ("Wino at a glance") in the menu bar popover, ported section by section from the
/// Windows CompanionFlyoutView: header with greeting, date, notification snooze and Settings; the
/// favorite contacts strip; the next event card with the rest of today; unread mail with hover
/// actions; My Day tasks; the caught-up state; and the app mode footer. 400pt wide and 460–720pt
/// tall like the Windows flyout. Bound to the shared <see cref="CompanionDashboardViewModel"/>.
/// </summary>
public sealed class CompanionPopoverViewController : NSViewController
{
    private static readonly NotificationSnoozePreset[] SnoozePresets =
    [
        NotificationSnoozePreset.ThirtyMinutes,
        NotificationSnoozePreset.OneHour,
        NotificationSnoozePreset.TwoHours,
        NotificationSnoozePreset.RestOfDay,
        NotificationSnoozePreset.UntilTomorrowMorning,
        NotificationSnoozePreset.UntilTurnedBackOn
    ];

    private readonly CompanionDashboardViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly IPictureStorageService _pictures;
    private readonly BindingScope _bindings = new();
    private readonly Dictionary<Guid, NSImage?> _pictureCache = new();
    private readonly List<INotifyPropertyChanged> _observedItems = new();
    private NSLayoutConstraint _height = null!;
    private NSView _root = null!;
    private WinoStateView _initializing = null!;
    private WinoStateView _noAccounts = null!;
    private WinoStateView _unavailable = null!;
    private WinoStateView _caughtUp = null!;
    private NSView _ready = null!;
    private NSView _header = null!;
    private NSTextField _greeting = null!;
    private NSTextField _date = null!;
    private NSButton _snoozeButton = null!;
    private WinoSurfaceView _snoozeInfo = null!;
    private NSTextField _snoozeInfoText = null!;
    private NSStackView _favorites = null!;
    private NSView _favoritesHost = null!;
    private NSLayoutConstraint _favoritesCollapsed = null!;
    private NSScrollView _scroll = null!;
    private NSStackView _content = null!;
    private NSView _footer = null!;
    private bool _rebuildQueued;
    private bool _released;

    public CompanionPopoverViewController(CompanionDashboardViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IPictureStorageService pictures)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _logger = logger;
        _pictures = pictures;
    }

    /// <summary>Raised after the preferred size changed, so the popover follows the content.</summary>
    public event EventHandler? PreferredSizeChanged;

    /// <summary>Raised on Escape (Windows Root_KeyDown).</summary>
    public event EventHandler? CloseRequested;

    [Export("cancelOperation:")]
    public void CancelOperation(NSObject? sender) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public override void LoadView()
    {
        _root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _root.WidthAnchor.ConstraintEqualTo((nfloat)CompanionStyle.Width).Active = true;
        _height = _root.HeightAnchor.ConstraintEqualTo((nfloat)CompanionStyle.MinHeight);
        _height.Active = true;
        WinoAccessibility.Label(_root, Translator.CompanionSettings_About_Title);

        _initializing = new WinoStateView(WinoIconGlyph.None, Translator.Companion_InitializingTitle, Translator.Companion_InitializingDescription);
        var spinner = _initializing.UseSpinner(32);
        spinner.StartAnimation(null);
        _noAccounts = new WinoStateView(WinoIconGlyph.Person, Translator.Companion_NoAccountsTitle, Translator.Companion_NoAccountsDescription);
        _noAccounts.Icon.PointSize = 44;
        _noAccounts.Add(CompanionStyle.AccentButton(Translator.Companion_OpenWino, () => Run(_viewModel.OpenWinoCommand.ExecuteAsync(null))));
        _unavailable = new WinoStateView(WinoIconGlyph.DismissCircle, Translator.Companion_UnavailableTitle, Translator.Companion_UnavailableDescription);
        _unavailable.Icon.PointSize = 44;
        var retry = new NSButton { Title = Translator.Companion_Retry, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        retry.Activated += (_, _) => Run(_viewModel.RefreshAsync(CancellationToken.None));
        _unavailable.Add(WinoLayout.HStack(8, retry, CompanionStyle.AccentButton(Translator.Companion_OpenWino, () => Run(_viewModel.OpenWinoCommand.ExecuteAsync(null)))));
        foreach (var state in new NSView[] { _initializing, _noAccounts, _unavailable })
            WinoLayout.Fill(state, _root);

        _ready = BuildReadyState();
        WinoLayout.Fill(_ready, _root);
        View = _root;

        Bind(nameof(CompanionDashboardViewModel.SurfaceState), vm => vm.SurfaceState, ApplySurfaceState);
        Bind(nameof(CompanionDashboardViewModel.GreetingText), vm => vm.GreetingText, value => _greeting.StringValue = value);
        Bind(nameof(CompanionDashboardViewModel.DateText), vm => vm.DateText, value => _date.StringValue = value);
        Bind(nameof(CompanionDashboardViewModel.SnoozeNotifications), vm => vm.SnoozeNotifications, ApplySnooze);
        Bind(nameof(CompanionDashboardViewModel.SnoozeInfoText), vm => vm.SnoozeInfoText, value => _snoozeInfoText.StringValue = value);
        Bind(nameof(CompanionDashboardViewModel.IsAllCaughtUp), vm => vm.IsAllCaughtUp, _ => QueueRebuild());

        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _viewModel.UnreadMail.CollectionChanged += CollectionChanged;
        _viewModel.Tasks.CollectionChanged += CollectionChanged;
        _viewModel.Favorites.CollectionChanged += CollectionChanged;
        _viewModel.LaterEvents.CollectionChanged += CollectionChanged;
        Rebuild();
    }

    /// <summary>Lays the content out for the current ViewModel state now, before the popover is shown.</summary>
    public void RefreshNow()
    {
        if (_released || !ViewLoaded) return;
        _rebuildQueued = false;
        Rebuild();
    }

    /// <summary>Stops following the ViewModel. The popover host calls it before disposing.</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        _bindings.Dispose();
        if (!ViewLoaded) return;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel.UnreadMail.CollectionChanged -= CollectionChanged;
        _viewModel.Tasks.CollectionChanged -= CollectionChanged;
        _viewModel.Favorites.CollectionChanged -= CollectionChanged;
        _viewModel.LaterEvents.CollectionChanged -= CollectionChanged;
        ObserveItems([]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }

    #region Layout

    private NSView BuildReadyState()
    {
        var ready = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        // Header: greeting and date, snooze, settings (Windows padding 12,13,8,9).
        _greeting = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(17, NSFontWeight.Semibold));
        _date = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.TertiaryText);
        _greeting.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _date.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var titles = WinoLayout.VStack(0, _greeting, _date);
        _snoozeButton = CompanionStyle.IconButton(WinoIconGlyph.Alert, Translator.Companion_SnoozeNotifications, ToggleSnooze, 32, 16);
        _snoozeButton.SetButtonType(NSButtonType.PushOnPushOff);
        var snoozeMenu = CompanionStyle.IconButton(WinoIconGlyph.ChevronDown, Translator.Companion_SnoozeNotifications, () => { }, 18, 10);
        snoozeMenu.Activated += (_, _) => ShowSnoozeMenu(snoozeMenu);
        var settings = CompanionStyle.IconButton(WinoIconGlyph.Settings, Translator.Companion_Settings, () => Run(_viewModel.OpenSettingsCommand.ExecuteAsync(null)), 32, 16);
        var headerRow = WinoLayout.HStack(4, titles, WinoLayout.Spacer(), WinoLayout.HStack(0, _snoozeButton, snoozeMenu), settings);

        _snoozeInfoText = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _snoozeInfo = new WinoSurfaceView { Fill = CompanionStyle.SubtleFill, CornerRadius = 4 };
        WinoLayout.Fill(WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.AlertOff, 13, WinoStyle.SecondaryText), _snoozeInfoText), _snoozeInfo, 6, 10, 6, 10);
        WinoAccessibility.Label(_snoozeInfo, Translator.Companion_NotificationsSnoozed);

        var headerStack = WinoLayout.VStack(10, headerRow, _snoozeInfo);
        headerStack.Alignment = NSLayoutAttribute.Leading;
        headerRow.WidthAnchor.ConstraintEqualTo(headerStack.WidthAnchor).Active = true;
        _snoozeInfo.TrailingAnchor.ConstraintEqualTo(headerStack.TrailingAnchor, -4).Active = true;
        _header = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(headerStack, _header, 13, 12, 9, 8);

        // Favorite contacts (Windows padding 12,2,12,12).
        _favorites = WinoLayout.HStack(4);
        _favorites.Alignment = NSLayoutAttribute.Top;
        _favoritesHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _favoritesHost.AddSubview(_favorites);
        // The vertical pins give way to the collapse constraint while there are no favorites.
        var favoritesTop = _favorites.TopAnchor.ConstraintEqualTo(_favoritesHost.TopAnchor, 2);
        var favoritesBottom = _favorites.BottomAnchor.ConstraintEqualTo(_favoritesHost.BottomAnchor, -12);
        favoritesTop.Priority = favoritesBottom.Priority = 999;
        NSLayoutConstraint.ActivateConstraints(
        [
            favoritesTop,
            favoritesBottom,
            _favorites.LeadingAnchor.ConstraintEqualTo(_favoritesHost.LeadingAnchor, (nfloat)CompanionStyle.SideInset),
            _favorites.TrailingAnchor.ConstraintLessThanOrEqualTo(_favoritesHost.TrailingAnchor, -(nfloat)CompanionStyle.SideInset)
        ]);
        _favoritesCollapsed = _favoritesHost.HeightAnchor.ConstraintEqualTo(0);
        _favorites.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Vertical);
        var divider = new WinoSeparator();

        // The scrolling event, mail and task content.
        _content = WinoLayout.VStack(0);
        _content.Alignment = NSLayoutAttribute.Leading;
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_content, document);
        _scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        NSLayoutConstraint.ActivateConstraints(
        [
            document.LeadingAnchor.ConstraintEqualTo(_scroll.ContentView.LeadingAnchor),
            document.TopAnchor.ConstraintEqualTo(_scroll.ContentView.TopAnchor),
            document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor)
        ]);

        _caughtUp = new WinoStateView(WinoIconGlyph.CheckmarkCircle, Translator.Companion_CaughtUpTitle, Translator.Companion_CaughtUpDescription, WinoStyle.Accent);
        _caughtUp.Icon.PointSize = 36;
        _caughtUp.Title.Font = NSFont.SystemFontOfSize(18, NSFontWeight.Semibold);
        _caughtUp.Caption.Font = WinoStyle.Body;

        _footer = BuildFooter();

        foreach (var view in new NSView[] { _header, _favoritesHost, divider, _scroll, _caughtUp, _footer })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            ready.AddSubview(view);
            NSLayoutConstraint.ActivateConstraints(
            [
                view.LeadingAnchor.ConstraintEqualTo(ready.LeadingAnchor),
                view.TrailingAnchor.ConstraintEqualTo(ready.TrailingAnchor)
            ]);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            _header.TopAnchor.ConstraintEqualTo(ready.TopAnchor),
            _favoritesHost.TopAnchor.ConstraintEqualTo(_header.BottomAnchor),
            divider.TopAnchor.ConstraintEqualTo(_favoritesHost.BottomAnchor),
            _scroll.TopAnchor.ConstraintEqualTo(divider.BottomAnchor, 6),
            _scroll.BottomAnchor.ConstraintEqualTo(_footer.TopAnchor),
            _caughtUp.TopAnchor.ConstraintEqualTo(divider.BottomAnchor),
            _caughtUp.BottomAnchor.ConstraintEqualTo(_footer.TopAnchor),
            _footer.BottomAnchor.ConstraintEqualTo(ready.BottomAnchor)
        ]);
        return ready;
    }

    private NSView BuildFooter()
    {
        var footer = new WinoSurfaceView { Fill = CompanionStyle.SubtleFill };
        var buttons = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Distribution = NSStackViewDistribution.FillEqually,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        buttons.AddArrangedSubview(new CompanionModeButton(WinoIconGlyph.AppModeMail, Translator.Companion_Mail, () => Run(_viewModel.OpenInboxCommand.ExecuteAsync(null))));
        buttons.AddArrangedSubview(new CompanionModeButton(WinoIconGlyph.AppModeCalendar, Translator.Companion_Calendar, () => Run(_viewModel.OpenCalendarCommand.ExecuteAsync(null))));
        buttons.AddArrangedSubview(new CompanionModeButton(WinoIconGlyph.AppModeContacts, Translator.Companion_Contacts, () => Run(_viewModel.FindAnyContactCommand.ExecuteAsync(null))));
        buttons.AddArrangedSubview(new CompanionModeButton(WinoIconGlyph.AppModeToDo, Translator.Companion_Tasks, () => Run(_viewModel.OpenTasksCommand.ExecuteAsync(null))));
        WinoLayout.Fill(buttons, footer, 10, 12, 12, 12);
        var border = new WinoSeparator();
        footer.AddSubview(border);
        NSLayoutConstraint.ActivateConstraints(
        [
            border.TopAnchor.ConstraintEqualTo(footer.TopAnchor),
            border.LeadingAnchor.ConstraintEqualTo(footer.LeadingAnchor),
            border.TrailingAnchor.ConstraintEqualTo(footer.TrailingAnchor)
        ]);
        return footer;
    }

    #endregion

    #region State

    private void ApplySurfaceState(CompanionSurfaceState state)
    {
        _initializing.Hidden = state != CompanionSurfaceState.Initializing;
        _noAccounts.Hidden = state != CompanionSurfaceState.NoAccounts;
        _unavailable.Hidden = state != CompanionSurfaceState.Unavailable;
        _ready.Hidden = state != CompanionSurfaceState.Ready;
        QueueRebuild();
    }

    private void ApplySnooze(bool snoozed)
    {
        _snoozeButton.State = snoozed ? NSCellStateValue.On : NSCellStateValue.Off;
        _snoozeButton.Image = WinoIcons.Image(snoozed ? WinoIconGlyph.AlertOff : WinoIconGlyph.Alert, 16, accessibilityDescription: _viewModel.SnoozeActionText);
        _snoozeButton.ToolTip = _viewModel.SnoozeActionText;
        WinoAccessibility.Label(_snoozeButton, _viewModel.SnoozeActionText);
        _snoozeInfo.Hidden = !snoozed;
        UpdatePreferredSize();
    }

    private void ToggleSnooze()
    {
        // The button itself is the open-ended choice: hold everything until turned back on.
        // Timed durations come from the menu, as with the Windows split button.
        Run(_viewModel.SnoozeNotifications
            ? _viewModel.ResumeNotificationsCommand.ExecuteAsync(null)
            : _viewModel.StartNotificationSnoozeCommand.ExecuteAsync(NotificationSnoozePreset.UntilTurnedBackOn));
    }

    private void ShowSnoozeMenu(NSView anchor)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        if (_viewModel.IsCustomSnoozeVisible)
        {
            // A custom snooze set in Settings is shown, checked, while it runs.
            menu.AddItem(new NSMenuItem(_viewModel.CustomSnoozeText) { State = NSCellStateValue.On, Enabled = false });
            menu.AddItem(NSMenuItem.SeparatorItem);
        }
        foreach (var preset in SnoozePresets)
        {
            var value = preset;
            menu.AddItem(new NSMenuItem(SnoozeTitle(value), (_, _) => Run(_viewModel.StartNotificationSnoozeCommand.ExecuteAsync(value))));
        }
        if (_viewModel.SnoozeNotifications)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(new NSMenuItem(Translator.Companion_ResumeNotifications, (_, _) => Run(_viewModel.ResumeNotificationsCommand.ExecuteAsync(null))));
        }
        PopUpBelow(menu, anchor);
    }

    private static string SnoozeTitle(NotificationSnoozePreset preset) => preset switch
    {
        NotificationSnoozePreset.ThirtyMinutes => Translator.NotificationSnooze_ThirtyMinutes,
        NotificationSnoozePreset.OneHour => Translator.NotificationSnooze_OneHour,
        NotificationSnoozePreset.TwoHours => Translator.NotificationSnooze_TwoHours,
        NotificationSnoozePreset.RestOfDay => Translator.NotificationSnooze_RestOfDay,
        NotificationSnoozePreset.UntilTomorrowMorning => Translator.NotificationSnooze_UntilTomorrowMorning,
        _ => Translator.NotificationSnooze_UntilTurnedBackOn
    };

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(CompanionDashboardViewModel.NextEvent):
            case nameof(CompanionDashboardViewModel.HasContent):
            case nameof(CompanionDashboardViewModel.UnreadTotalText):
            case nameof(CompanionDashboardViewModel.TaskProgressText):
                QueueRebuild();
                break;
        }
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRebuild();

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TaskItemViewModel.IsCompleted) or nameof(AccountContactViewModel.UnreadCount) or nameof(MailItemViewModel.IsRead))
            QueueRebuild();
    }

    /// <summary>Coalesces collection and item changes into one rebuild on the UI thread.</summary>
    private void QueueRebuild()
    {
        if (_released || _rebuildQueued) return;
        _rebuildQueued = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            if (!_released) Rebuild();
        });
    }

    private void Rebuild()
    {
        try
        {
            RebuildFavorites();
            RebuildContent();
            var showContent = _viewModel.HasContent;
            _scroll.Hidden = !showContent;
            _caughtUp.Hidden = showContent || !_viewModel.IsAllCaughtUp;
            ObserveItems(_viewModel.Tasks.Cast<INotifyPropertyChanged>().Concat(_viewModel.Favorites).Concat(_viewModel.UnreadMail));
            UpdatePreferredSize();
        }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(CompanionPopoverViewController)); }
    }

    private void ObserveItems(IEnumerable<INotifyPropertyChanged> items)
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemPropertyChanged;
        _observedItems.Clear();
        foreach (var item in items)
        {
            item.PropertyChanged += ItemPropertyChanged;
            _observedItems.Add(item);
        }
    }

    private void RebuildFavorites()
    {
        Clear(_favorites);
        foreach (var contact in _viewModel.Favorites.ToArray())
        {
            var item = contact;
            _favorites.AddArrangedSubview(new CompanionContactButton(item, LoadPicture(item), () => Run(_viewModel.FindContactCommand.ExecuteAsync(item))));
        }
        // A hidden view still takes its height in plain constraint layout, so collapse it as well.
        _favoritesHost.Hidden = _viewModel.Favorites.Count == 0;
        _favoritesCollapsed.Active = _favoritesHost.Hidden;
    }

    private void RebuildContent()
    {
        Clear(_content);
        if (_viewModel.NextEvent is { } nextEvent)
        {
            AddContent(BuildEventSection(nextEvent), 0, 0, 10);
        }
        if (_viewModel.ShowEventMailSeparator) AddContent(new WinoSeparator(), 2, 0, 8, CompanionStyle.SideInset);
        if (_viewModel.HasUnreadMail) AddContent(BuildMailSection(), 0, 0, 6);
        if (_viewModel.ShowMailTaskSeparator) AddContent(new WinoSeparator(), 2, 0, 8, CompanionStyle.SideInset);
        if (_viewModel.HasTasks) AddContent(BuildTaskSection(), 0, 0, 8);
    }

    private void AddContent(NSView view, double top, double leading, double bottom, double inset = 0)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, host, top, inset, bottom, inset);
        _content.AddArrangedSubview(host);
        host.WidthAnchor.ConstraintEqualTo(_content.WidthAnchor).Active = true;
    }

    private NSView BuildEventSection(CalendarItemViewModel item)
    {
        // Countdown, online badge and the more-actions menu.
        var countdownText = WinoStyle.Label(_viewModel.NextEventCountdownText, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), CompanionStyle.OnAccent);
        var countdown = CompanionStyle.Pill(countdownText, WinoStyle.Accent);
        var badges = WinoLayout.HStack(7, countdown);
        if (_viewModel.IsNextEventOnline)
        {
            var online = CompanionStyle.Pill(WinoStyle.Label(Translator.Companion_EventOnline, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)), CompanionStyle.SubtleFill,
                icon: new WinoIconView(WinoIconGlyph.Link, 11));
            badges.AddArrangedSubview(online);
        }
        NSButton? more = null;
        more = CompanionStyle.IconButton(WinoIconGlyph.More, Translator.Companion_EventMoreActions, () => ShowEventMenu(more!, item), 28, 14);
        var topRow = WinoLayout.HStack(7, badges, WinoLayout.Spacer(), more);

        var title = WinoStyle.Label(item.Title, NSFont.SystemFontOfSize(16, NSFontWeight.Semibold));
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var time = WinoStyle.Label(_viewModel.NextEventTimeText, NSFont.SystemFontOfSize((nfloat)12.5), WinoStyle.SecondaryText);
        var timeRow = WinoLayout.HStack(8, time, WinoLayout.Spacer());
        if (_viewModel.HasNextEventCalendar)
        {
            var dot = new WinoSurfaceView { Fill = WinoStyle.FromHexString(_viewModel.NextEventCalendarColorHex) ?? WinoStyle.Accent, CornerRadius = 2 };
            WinoLayout.Size(dot, 8, 8);
            var calendarName = WinoStyle.Label(_viewModel.NextEventCalendarName, WinoStyle.Description, WinoStyle.TertiaryText);
            calendarName.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            timeRow.AddArrangedSubview(WinoLayout.HStack(6, dot, calendarName));
        }

        var card = WinoLayout.VStack(6, topRow, title, timeRow);
        if (_viewModel.HasNextEventLocation)
        {
            var location = WinoStyle.Label(item.Location, NSFont.SystemFontOfSize((nfloat)12.5), WinoStyle.SecondaryText);
            location.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            card.AddArrangedSubview(location);
        }
        var attendees = WinoStyle.Label(_viewModel.NextEventAttendeesText, WinoStyle.Description, WinoStyle.TertiaryText);
        attendees.Hidden = !_viewModel.HasNextEventAttendees;
        var primary = CompanionStyle.AccentButton(_viewModel.NextEventPrimaryActionText, () => Run(_viewModel.OpenPrimaryEventCommand.ExecuteAsync(item)));
        var actionRow = WinoLayout.HStack(8, attendees, WinoLayout.Spacer(), primary);
        card.AddArrangedSubview(actionRow);
        card.SetCustomSpacing(11, card.ArrangedSubviews[^2]);
        foreach (var row in card.ArrangedSubviews)
            row.WidthAnchor.ConstraintEqualTo(card.WidthAnchor).Active = true;

        var surface = new WinoSurfaceView { Fill = CompanionStyle.CardFill, Stroke = WinoStyle.Accent, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(card, surface, 13, 14, 13, 14);
        WinoAccessibility.Label(surface, $"{item.Title}, {_viewModel.NextEventTimeText}");

        var section = WinoLayout.VStack(6, surface);
        if (_viewModel.HasLaterEvents)
        {
            var laterButton = new NSButton
            {
                Title = _viewModel.LaterEventsText,
                Image = WinoIcons.Image(WinoIconGlyph.ChevronRight, 12),
                ImagePosition = NSCellImagePosition.ImageLeading,
                BezelStyle = NSBezelStyle.Rounded,
                Font = NSFont.SystemFontOfSize((nfloat)12.5, NSFontWeight.Semibold),
                Alignment = NSTextAlignment.Left,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            laterButton.Activated += (_, _) => ShowLaterEventsMenu(laterButton);
            section.AddArrangedSubview(laterButton);
        }
        foreach (var row in section.ArrangedSubviews)
            row.WidthAnchor.ConstraintEqualTo(section.WidthAnchor).Active = true;
        return Inset(section);
    }

    private void ShowEventMenu(NSView anchor, CalendarItemViewModel item)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Companion_OpenEvent, (_, _) => Run(_viewModel.OpenCalendarEventCommand.ExecuteAsync(item)))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Open, 14)
        });
        menu.AddItem(new NSMenuItem(Translator.Companion_OpenCalendar, (_, _) => Run(_viewModel.OpenCalendarCommand.ExecuteAsync(null)))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Calendar, 14)
        });
        PopUpBelow(menu, anchor);
    }

    /// <summary>The rest of today (Windows: a flyout listing the later events).</summary>
    private void ShowLaterEventsMenu(NSView anchor)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Companion_RestOfToday) { Enabled = false });
        foreach (var later in _viewModel.LaterEvents.ToArray())
        {
            var item = later;
            var title = string.IsNullOrWhiteSpace(item.Location) ? item.Title : $"{item.Title} · {item.Location}";
            menu.AddItem(new NSMenuItem($"{item.DisplayStartTime}  {title}", (_, _) => Run(_viewModel.OpenCalendarEventCommand.ExecuteAsync(item))));
        }
        PopUpBelow(menu, anchor);
    }

    private NSView BuildMailSection()
    {
        var header = WinoStyle.Label(Translator.Companion_Unread, CompanionStyle.SectionHeader, WinoStyle.TertiaryText);
        var countText = WinoStyle.Label(_viewModel.UnreadTotalText, NSFont.SystemFontOfSize(11, NSFontWeight.Bold), CompanionStyle.OnAccent);
        var count = CompanionStyle.Pill(countText, WinoStyle.Accent, 6, 1);
        var headerRow = WinoLayout.HStack(8, header, count, WinoLayout.Spacer());
        var section = WinoLayout.VStack(2, headerRow);
        section.SetCustomSpacing(6, headerRow);
        foreach (var mail in _viewModel.UnreadMail.ToArray())
        {
            var item = mail;
            section.AddArrangedSubview(new CompanionMailRowView(item,
                () => Run(_viewModel.OpenMailCommand.ExecuteAsync(item)),
                () => Run(_viewModel.ArchiveMailCommand.ExecuteAsync(item)),
                () => Run(_viewModel.MarkMailReadCommand.ExecuteAsync(item))));
        }
        foreach (var row in section.ArrangedSubviews)
            row.WidthAnchor.ConstraintEqualTo(section.WidthAnchor).Active = true;
        return Inset(section);
    }

    private NSView BuildTaskSection()
    {
        var header = WinoStyle.Label(Translator.Companion_MyDay, CompanionStyle.SectionHeader, WinoStyle.TertiaryText);
        var progress = WinoStyle.Label(_viewModel.TaskProgressText, WinoStyle.Description, WinoStyle.TertiaryText);
        var headerRow = WinoLayout.HStack(8, header, progress, WinoLayout.Spacer());
        var section = WinoLayout.VStack(2, headerRow);
        section.SetCustomSpacing(4, headerRow);
        foreach (var task in _viewModel.Tasks.ToArray())
        {
            var item = task;
            section.AddArrangedSubview(new CompanionTaskRowView(item, () => Run(_viewModel.SetTaskCompletedCommand.ExecuteAsync(item))));
        }
        foreach (var row in section.ArrangedSubviews)
            row.WidthAnchor.ConstraintEqualTo(section.WidthAnchor).Active = true;
        return Inset(section);
    }

    /// <summary>Opens <paramref name="menu"/> under <paramref name="anchor"/>, like a pull-down.</summary>
    private static void PopUpBelow(NSMenu menu, NSView anchor)
        => menu.PopUpMenu(null, new CGPoint(0, anchor.IsFlipped ? anchor.Bounds.Height + 4 : -4), anchor);

    private static NSView Inset(NSView view)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, host, 0, CompanionStyle.SideInset, 0, CompanionStyle.SideInset);
        return host;
    }

    private static void Clear(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    /// <summary>Sizes the popover to its content within the Windows 460–720pt bounds.</summary>
    private void UpdatePreferredSize()
    {
        if (!ViewLoaded) return;
        double height = CompanionStyle.MinHeight;
        if (_viewModel.SurfaceState == CompanionSurfaceState.Ready)
        {
            _content.LayoutSubtreeIfNeeded();
            double content = _scroll.Hidden ? (_caughtUp.Hidden ? 0 : _caughtUp.FittingSize.Height + 48) : _content.FittingSize.Height + 6;
            height = _header.FittingSize.Height
                + (_favoritesHost.Hidden ? 0 : _favoritesHost.FittingSize.Height)
                + 1 + content + _footer.FittingSize.Height;
        }
        height = Math.Clamp(Math.Ceiling(height), CompanionStyle.MinHeight, CompanionStyle.MaxHeight);
        if (Math.Abs(_height.Constant - height) < 0.5 && PreferredContentSize.Height > 0) return;
        _height.Constant = (nfloat)height;
        PreferredContentSize = new CGSize(CompanionStyle.Width, height);
        PreferredSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    /// <summary>The contact's stored photo, cached per file id; null falls back to initials.</summary>
    private NSImage? LoadPicture(AccountContactViewModel contact)
    {
        if (contact.ContactPictureFileId is not { } id) return null;
        if (_pictureCache.TryGetValue(id, out var cached)) return cached;
        NSImage? image = null;
        try
        {
            var path = _pictures.GetPicturePath(PictureKind.Contact, id);
            if (File.Exists(path)) image = new NSImage(path);
        }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(CompanionPopoverViewController)); }
        _pictureCache[id] = image;
        return image;
    }

    private void Bind<TValue>(string property, Func<CompanionDashboardViewModel, TValue> read, Action<TValue> apply)
        => _bindings.Own(new PropertyBinding<CompanionDashboardViewModel, TValue>(_viewModel, property, read, apply, _dispatcher,
            exception => _logger.CaptureException(exception, nameof(CompanionPopoverViewController))));

    private async void Run(Task task)
    {
        try { await task; }
        catch (Exception exception) { _viewModel.ReportActionError(exception); }
    }

    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
