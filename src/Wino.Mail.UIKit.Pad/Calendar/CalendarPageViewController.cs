using CoreGraphics;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Mail.UIKit.Pad.Shell;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>
/// The iPad Calendar mode (Windows CalendarPage layout, macOS design Revision 2): the period surface (Day, Week, Work
/// week, Month) fills the content column, which the pad shell widens in Calendar mode so the reader column becomes the
/// right-side event details zone; previous/next and Today join the toolbar row's leading items, and the range and the
/// period control sit in the row (wide column) or in the zone's header (narrow column, the shell's narrow toolbar rule).
/// A tile tap shows the details in the reader column, the composer opens in place over the surface (this page is the
/// rendering frame for it), and an empty slot (tap or long-press range) opens the quick event popover. The sidebar's
/// content slot shows <see cref="PadCalendarSidebarViewController"/>. Partial files: Layout (the toolbar rule and the
/// sidebar content), Search (toolbar search), Debug.
/// </summary>
public sealed partial class CalendarPageViewController : CalendarPageViewControllerBase
{
    private CalendarPage? _page;
    private UINavigationController? _composerNavigation;
    private CalendarEventComposePageViewController? _composer;

    public CalendarPageViewController(CalendarPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        CalendarAppShellViewModel shell, IContextMenuItemService contextMenus, IDateContextProvider dateContext, IMailDialogService dialogs,
        IServiceProvider services)
        : base(viewModel, dispatcher, logger, shell, contextMenus, dateContext, dialogs, services)
    {
        Title = Translator.KeyboardShortcuts_ModeCalendar;
    }

    private PadShellViewController? PadShell => Navigation?.Shell as PadShellViewController;

    public override void LoadView()
    {
        var page = new CalendarPage();
        _page = page;
        PlaceSurface(page.SurfaceHost);
        page.RaiseComposerHost();
        foreach (var control in new[] { page.Period, page.HeaderPeriod })
        {
            control.ValueChanged += (_, _) =>
            {
                var index = (int)control.SelectedSegment;
                if (index >= 0 && index < CalendarPage.DisplayTypes.Length) RequestDisplayType(CalendarPage.DisplayTypes[index]);
            };
        }
        View = page;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BuildToolbarItems();
        AttachSidebar();
        return base.InitializeAsync(mode, parameter);
    }

    /// <summary>
    /// The toolbar row (Mac CalendarToolbarView): the shell's sidebar toggle and sync stay first, then previous/next, the
    /// range and Today; the period control sits on the trailing side over the content column.
    /// </summary>
    private void BuildToolbarItems()
    {
        if (_page is not { } page) return;
        var leading = new List<UIBarButtonItem>(PadShell?.LeadingItems ?? NavigationItem.LeftBarButtonItems ?? []);
        leading.Add(BarButton(WinoIconGlyph.ArrowLeft, Translator.Accessibility_PreviousCalendarPeriod, GoPrevious));
        leading.Add(BarButton(WinoIconGlyph.ArrowRight, Translator.Accessibility_NextCalendarPeriod, GoNext));
        leading.Add(new UIBarButtonItem(Translator.Today, UIBarButtonItemStyle.Plain, (_, _) => GoToday()));
        NavigationItem.LeftBarButtonItems = leading.ToArray();
        // The range replaces the folder title of the mail list (Mac: 18pt semibold range text next to the arrows).
        ApplyPeriodItems();
    }

    private bool? _periodCompact;

    /// <summary>
    /// The pad shell's narrow toolbar rule. Wide content column (landscape): the Mac row, with the range title, the period
    /// control and New event. Narrow (13-inch portrait leaves about 430pt; measured inside the safe area, because the column view extends under the reader column): the row keeps the shell's sidebar toggle and sync
    /// with previous, next and Today; the range and the period control move into the zone's header, and New event stays in
    /// the calendar sidebar.
    /// </summary>
    private void ApplyPeriodItems()
    {
        if (_page is not { } page) return;
        bool compact = PadShellViewController.IsCompactContentWidth(View?.SafeAreaLayoutGuide.LayoutFrame.Width ?? 0);
        if (_periodCompact == compact) return;
        _periodCompact = compact;
        page.Header.Hidden = !compact;
        NavigationItem.TitleView = compact ? null : page.RangeLabel;
        NavigationItem.Title = compact ? string.Empty : Title;
        NavigationItem.RightBarButtonItems = compact
            ? []
            : [BarButton(WinoIconGlyph.Add, Translator.CalendarEventCompose_NewEventButton, NewEvent), new UIBarButtonItem(page.Period)];
    }

    private static UIBarButtonItem BarButton(WinoIconGlyph glyph, string label, Action action)
    {
        var item = new UIBarButtonItem(WinoIcons.Image(glyph, 18, WinoStyle.PrimaryText), UIBarButtonItemStyle.Plain, (_, _) => action());
        item.AccessibilityLabel = label;
        return item;
    }

    protected override EventDetailsPageViewController? CurrentDetails => PadShell?.ReaderFrame.Page as EventDetailsPageViewController;

    /// <summary>iPad: a tap or a long-press range on empty time opens the quick event popover at the slot (Mac twin).</summary>
    protected override (UIView? View, CGRect Rect)? QuickEventAnchor(CalendarSlotClickedEventArgs args) => (Surface, args.Anchor);

    protected override async Task DetailsClearedAsync()
    {
        if (PadShell is { } shell && shell.ReaderFrame.Page is EventDetailsPageViewController) await shell.ReaderFrame.ClearAsync();
    }

    protected override void VisibleRangeChanged(VisibleDateRange? range) => _sidebar?.SetRange(range);

    protected override void SettingsChanged(CalendarSettings? settings)
    {
        if (settings is not null) _sidebar?.SetFirstDayOfWeek(settings.FirstDayOfWeek);
    }

    protected override void DisplayTypeChanged(CalendarDisplayType type)
    {
        if (_page is { } page) page.SelectPeriod(Array.IndexOf(CalendarPage.DisplayTypes, type));
    }

    protected override void RangeTextChanged(string? text)
    {
        _page?.SetRange(text ?? string.Empty);
    }

    // ------------------------------------------------------------ composer in place

    /// <summary>The event composer this page took from the rendering frame: in place over the surface.</summary>
    protected override bool ShowComposer(CalendarEventComposePageViewController composer)
    {
        if (Released || _page is not { } page || !IsViewLoaded) return false;
        CloseQuickEvent();
        RemoveComposer();
        var navigation = new UINavigationController(composer);
        if (navigation.View is { } navigationView) navigationView.BackgroundColor = UIColor.Clear;
        AddChildViewController(navigation);
        if (navigation.View is { } view) WinoLayout.Fill(view, page.ComposerHost);
        navigation.DidMoveToParentViewController(this);
        page.ComposerHost.Hidden = false;
        Surface.Hidden = true;
        _composerNavigation = navigation;
        _composer = composer;
        composer.CloseRequested += ComposerCloseRequested;
        return true;
    }

    private void ComposerCloseRequested(object? sender, EventArgs args) => Observe(CloseComposerAsync());

    private async Task CloseComposerAsync()
    {
        var composer = _composer;
        if (composer is null) return;
        RemoveComposer();
        await CalendarComposerSheet.ReleaseAsync(Services, composer);
    }

    /// <summary>Takes the composer off screen; the caller releases it.</summary>
    private void RemoveComposer()
    {
        if (_composer is { } composer) composer.CloseRequested -= ComposerCloseRequested;
        _composer = null;
        if (_composerNavigation is { } navigation)
        {
            navigation.WillMoveToParentViewController(null);
            navigation.View?.RemoveFromSuperview();
            navigation.RemoveFromParentViewController();
            _composerNavigation = null;
        }
        if (_page is { } page) page.ComposerHost.Hidden = true;
        Surface.Hidden = ViewModel.Readiness.IsBlocked;
    }

    protected override async Task ReleaseHostedPagesAsync()
    {
        CalendarEventComposePageViewController? composer = null;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            composer = _composer;
            RemoveComposer();
        });
        if (composer is not null) await CalendarComposerSheet.ReleaseAsync(Services, composer);
        await DetailsClearedAsync();
    }

    protected override void ReadinessChanged(bool blocked)
    {
        if (_composer is not null) Surface.Hidden = true;
    }

    protected override void ReleaseIdiom()
    {
        ReleaseSearch();
        DetachSidebar();
    }
}
