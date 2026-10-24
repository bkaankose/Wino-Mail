using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Controls;
using IMenuItem = Wino.Core.Domain.Interfaces.IMenuItem;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>What a shell content page can ask of the shell around it.</summary>
public interface IShellChrome
{
    /// <summary>The window width class the shell is laid out for.</summary>
    WindowWidthClass WidthClass { get; }

    /// <summary>Opens the navigation drawer (compact, medium) or shows and hides the pinned pane (expanded).</summary>
    void ToggleNavigation();

    /// <summary>The view snackbars sit above (the compact New mail FAB while it shows), else null.</summary>
    View? SnackbarAnchor { get; }

    /// <summary>Hides the compact New mail FAB while the page shows its own full-screen state (search; canvas Search).</summary>
    void SuppressFab(bool suppressed);
}

/// <summary>
/// A content page that spans the list and reader columns on medium and expanded widths (the Windows calendar surface):
/// the content column fills the width and the reader column becomes a fixed details pane.
/// </summary>
public interface IShellWideContent
{
    /// <summary>The details pane width resource for <paramref name="widthClass"/>.</summary>
    int DetailsPaneWidthResource(WindowWidthClass widthClass);
}

/// <summary>
/// The app shell (plan section 1, canvas boards Inbox, Mailboxes, Landscape and Tablet) over <see cref="AndroidShellController"/>.
/// The layout comes from the window size class through resource qualifiers: compact has bottom navigation, the New mail
/// FAB and a modal drawer; medium (layout-w600dp) a navigation rail with the list and reader panes and the drawer for
/// folders; expanded (layout-w1200dp) the Windows layout with the pinned pane and the mode switcher card. The shell
/// places the navigation service's entries: the mode's content page in the list pane and, on medium and expanded widths,
/// the rendering-frame page (the reader) in the reader pane. The account avatar opens Settings; Settings is never a mode.
/// </summary>
public sealed class ShellFragment : WinoFragment, IShellChrome
{
    private IReadOnlyList<WinoModeSegment> _segments = WinoModeSegments.AppModes;
    private AndroidNavigationService? _navigation;
    private AndroidShellController? _shell;
    private ShellMenuAdapter? _menuAdapter;
    private DrawerLayout? _drawer;
    private View? _pane;
    private BottomNavigationView? _bottomNav;
    private NavigationRailView? _rail;
    private LinearLayout? _modeCard;
    private TextView? _panelSubtitle;
    private View? _readerIdle;
    private bool _readerPane;

    protected override int LayoutResource => Resource.Layout.fragment_shell;

    public WindowWidthClass WidthClass { get; private set; }

    protected override void OnBind(View view)
    {
        var services = MainApplication.Services;
        _navigation = services?.GetService<AndroidNavigationService>();
        _shell = _navigation?.Shell;
        if (services is null || _navigation is null || _shell is null) return;
        var context = view.RequireContext();
        WidthClass = services.GetRequiredService<IWindowSizeClassService>().WidthClass;

        _drawer = view.FindViewById<DrawerLayout>(Resource.Id.shell_drawer);
        _pane = view.FindViewById(Resource.Id.shell_pane);
        _readerIdle = view.FindViewById(Resource.Id.shell_reader_idle);
        _readerPane = view.FindViewById(Resource.Id.shell_reader_host) is not null;
        if (view.FindViewById<WinoIconView>(Resource.Id.shell_reader_idle_glyph) is { } idleGlyph)
        {
            idleGlyph.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_display);
            idleGlyph.Icon = WinoIconGlyph.MailFilled;
        }
        ApplyInsets(view);

        BuildPanel(view, context, services);
        BuildModeSwitchers(view, context);
        ApplyModeVisibility();

        if (view.FindViewById<FloatingActionButton>(Resource.Id.shell_fab) is { } fab)
        {
            fab.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Edit, WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
            WinoMaterialAccent.Tint(fab);
            fab.ContentDescription = Translator.MenuNewMail;
            EventHandler compose = (_, _) => _shell.Compose();
            fab.Click += compose;
            Bindings.Own(new ActionDisposable(() => fab.Click -= compose));
        }

        _shell.Changed += ShellChanged;
        _navigation.ShellPagesChanged += PagesChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            CloseDrawer(animate: false);
            _shell.Changed -= ShellChanged;
            _navigation.ShellPagesChanged -= PagesChanged;
            _menuAdapter?.Detach();
        }));
        ApplyMenu();
        ApplyMode();
        PlacePages();

        // The only notification permission request (Android 13+): the first shell asks once.
        if (Activity is { } activity && services.GetService<AndroidNotificationPermission>() is { } permission)
            _ = permission.RequestOnceAsync(activity);
    }

    /// <summary>
    /// Edge to edge: the panes start under the status bar; the bottom bar pads itself above the gesture area, wider
    /// layouts pad the whole shell.
    /// </summary>
    private void ApplyInsets(View view)
    {
        var root = view.FindViewById(Resource.Id.shell_root) ?? view;
        // Shell-wide rule on compact widths: the bottom navigation (with its backdrop) steps aside while the keyboard is
        // open in any mode, as Material asks; the page's own bars (compose format bar, add-task bar) sit on the keyboard.
        var bottomBar = view.FindViewById(Resource.Id.shell_bottom_bar);
        WinoInsets.Apply(root, top: true, bottom: WidthClass != WindowWidthClass.Compact,
            keyboardChanged: bottomBar is null ? null : keyboard => bottomBar.Post(() =>
                bottomBar.Visibility = keyboard ? ViewStates.Gone : ViewStates.Visible));
        // The drawer's NavigationView keeps the insets for its scrim; the pane content is padded through it.
        if (view.FindViewById(Resource.Id.shell_navigation) is { } navigation) WinoInsets.Apply(navigation);
    }

    // ---- Navigation pane (drawer or pinned pane) ----

    private void BuildPanel(View view, Context context, IServiceProvider services)
    {
        if (view.FindViewById<TextView>(Resource.Id.shell_panel_title) is { } title) title.Text = "Wino Mail";
        _panelSubtitle = view.FindViewById<TextView>(Resource.Id.shell_panel_subtitle);
        foreach (var (avatarId, glyphSize) in new[] { (Resource.Id.shell_avatar, Resource.Dimension.wino_m3_drawer_avatar_glyph), (Resource.Id.shell_rail_avatar, Resource.Dimension.wino_m3_rail_avatar_glyph) })
        {
            if (view.FindViewById<WinoIconView>(avatarId) is not { } avatar) continue;
            avatar.Icon = WinoIconGlyph.Person;
            avatar.GlyphSize = WinoTokens.Dp(context, glyphSize);
            // The theme accent with its readable glyph colour (WinoIconView starts in the secondary text colour).
            avatar.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.Accent(context));
            avatar.SetTextColor(WinoColors.OnAccent(context));
            avatar.ContentDescription = Translator.MenuSettings;
            avatar.Clickable = true;
            EventHandler open = (_, _) =>
            {
                CloseDrawer(animate: false);
                _shell?.OpenSettings();
            };
            avatar.Click += open;
            Bindings.Own(new ActionDisposable(() => avatar.Click -= open));
        }

        if (view.FindViewById<WinoIconView>(Resource.Id.shell_panel_settings) is { } settings)
        {
            settings.Icon = WinoIconGlyph.Settings;
            settings.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
            settings.ContentDescription = Translator.MenuSettings;
            AndroidX.Core.View.ViewCompat.SetTooltipText(settings, Translator.MenuSettings);
            EventHandler openSettings = (_, _) =>
            {
                CloseDrawer(animate: false);
                _shell?.OpenSettings();
            };
            settings.Click += openSettings;
            Bindings.Own(new ActionDisposable(() => settings.Click -= openSettings));
        }

        if (view.FindViewById<RecyclerView>(Resource.Id.shell_menu) is not { } list) return;
        _menuAdapter = new ShellMenuAdapter(services.GetService<IPictureStorageService>());
        list.SetLayoutManager(new LinearLayoutManager(context));
        list.SetAdapter(_menuAdapter);
        _menuAdapter.Invoked += (_, item) => RowInvoked(item);
        _menuAdapter.LongPressed += (_, args) => ShellMenuActions.Show(context, args.Anchor, args.Item, services);
    }

    private void RowInvoked(IMenuItem item)
    {
        if (_shell is null || !ShellMenuItemRules.IsInteractive(item) || !ShellMenuItemRules.IsEnabled(item)) return;
        // Parents that only group their children expand in place; everything else navigates and closes the drawer.
        if (ShellMenuRowPresentation.HasChildren(item) && item is not IAccountMenuItem && !ShellMenuItemRules.SelectsOnInvoked(item))
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }
        // Calendar check boxes toggle in place; the drawer stays open for the next one.
        if (Calendar.CalendarPaneRows.ViewTypeOf(item) == 0) CloseDrawer();
        _shell.SelectMenuItem(item);
    }

    private void ApplyMenu()
    {
        if (_shell is null || _menuAdapter is null) return;
        _menuAdapter.SetMenu(_shell.ViewModel.CurrentMenu);
        _menuAdapter.SetSelected(_shell.ViewModel.SelectedMenuItem);
    }

    private void ShellChanged(object? sender, string property)
    {
        if (View is null || _shell is null) return;
        switch (property)
        {
            case nameof(_shell.ViewModel.CurrentMenu) or nameof(_shell.ViewModel.CurrentProvider) or "":
                ApplyMenu();
                ApplyStatus();
                break;
            case nameof(_shell.ViewModel.SelectedMenuItem):
                _menuAdapter?.SetSelected(_shell.ViewModel.SelectedMenuItem);
                break;
            case nameof(_shell.ViewModel.CurrentMode) or nameof(AndroidShellController.ActiveMode):
                ApplyMode();
                break;
            case nameof(_shell.ViewModel.ModeVisibility):
                ApplyModeVisibility();
                break;
            case nameof(IShellMenuProvider.SynchronizationState):
                ApplyStatus();
                break;
        }
    }

    /// <summary>The pane header's second line: the running synchronization, else the mode.</summary>
    private void ApplyStatus()
    {
        if (_panelSubtitle is null || _shell is null) return;
        var provider = _shell.CurrentProvider;
        _panelSubtitle.Text = provider is { SynchronizationState.IsSynchronizing: true } && !string.IsNullOrWhiteSpace(provider.SynchronizationDescription)
            ? provider.SynchronizationDescription
            : Wino.Shell.ViewModels.ShellModeActivator.ModeTitle(_shell.ActiveMode ?? WinoApplicationMode.Mail);
    }

    private bool _fabSuppressed;

    public void SuppressFab(bool suppressed)
    {
        _fabSuppressed = suppressed;
        ApplyMode();
    }

    public View? SnackbarAnchor => View?.FindViewById(Resource.Id.shell_fab) is { Visibility: ViewStates.Visible } fab ? fab : null;

    public void ToggleNavigation()
    {
        if (_drawer is { } drawer)
        {
            if (drawer.IsDrawerOpen(GravityCompat.Start)) drawer.CloseDrawer(GravityCompat.Start);
            else drawer.OpenDrawer(GravityCompat.Start);
            return;
        }
        if (_pane is { } pane) pane.Visibility = pane.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>
    /// Closes the drawer. Before the shell leaves the screen (Settings) it closes without animation: an open drawer
    /// keeps its own back callback registered with the window until its close animation ends.
    /// </summary>
    private void CloseDrawer(bool animate = true) => _drawer?.CloseDrawer(GravityCompat.Start, animate);

    // ---- Modes: bottom navigation (compact), rail (medium), switcher card (expanded) ----

    private void BuildModeSwitchers(View view, Context context)
    {
        _bottomNav = view.FindViewById<BottomNavigationView>(Resource.Id.shell_bottom_nav);
        _rail = view.FindViewById<NavigationRailView>(Resource.Id.shell_rail);
        foreach (var bar in new NavigationBarView?[] { _bottomNav, _rail })
        {
            if (bar is null) continue;
            WinoMaterialAccent.Tint(bar);
            EventHandler<NavigationBarView.ItemSelectedEventArgs> selected = (_, args) =>
            {
                args.Handled = true;
                if (ModeOf(args.Item.ItemId) is { } mode) _shell?.RequestMode(mode);
            };
            bar.ItemSelected += selected;
            Bindings.Own(new ActionDisposable(() => bar.ItemSelected -= selected));
        }

        if (_rail is not null)
        {
            // The rail header: New mail (canvas Landscape). The list's app bar keeps the drawer button, so the rail fits a
            // landscape phone's height with the avatar at its foot.
            var header = new LinearLayout(context) { Orientation = Orientation.Vertical };
            header.SetGravity(GravityFlags.CenterHorizontal);
            // The rail's tonal FAB (Material 3: primary container, 16 dp corners from the theme's FAB style).
            var fab = new FloatingActionButton(context) { ContentDescription = Translator.MenuNewMail };
            fab.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Edit, WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
            WinoMaterialAccent.Tint(fab);
            fab.Click += (_, _) => _shell?.Compose();
            var fabParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_m3_rail_gap) };
            header.AddView(fab, fabParams);
            _rail.AddHeaderView(header);
        }

        _modeCard = view.FindViewById<LinearLayout>(Resource.Id.shell_mode_card);
        if (_modeCard is not null && _pane is not null) _modeCard.Visibility = ViewStates.Visible;
        PopulateModeSwitchers(context);
    }

    /// <summary>Fills the bars and the switcher card with the current segments (every visible mode, then Settings on the card).</summary>
    private void PopulateModeSwitchers(Context context)
    {
        foreach (var bar in new NavigationBarView?[] { _bottomNav, _rail })
        {
            if (bar is null) continue;
            bar.Menu.Clear();
            for (int index = 0; index < _segments.Count; index++)
            {
                var segment = _segments[index];
                var menuItem = bar.Menu.Add(0, ItemId(segment.Mode), index, segment.Title);
                menuItem?.SetIcon(WinoIcons.Drawable(context, segment.Regular, WinoTokens.Dp(context, Resource.Dimension.wino_m3_nav_icon)));
            }
        }

        if (_modeCard is not null && _pane is not null)
        {
            // Expanded: the Windows mode switcher card at the foot of the pane, then the avatar for Settings.
            _modeCard.RemoveAllViews();
            foreach (var segment in _segments)
            {
                var button = new WinoIconView(context) { Icon = segment.Regular, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_l), ContentDescription = segment.Title, Clickable = true, Gravity = GravityFlags.Center, Tag = (int)segment.Mode };
                var mode = segment.Mode;
                button.Click += (_, _) => _shell?.RequestMode(mode);
                _modeCard.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
            }
            var settings = new WinoIconView(context) { Icon = WinoIconGlyph.Settings, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_l), ContentDescription = Translator.MenuSettings, Clickable = true, Gravity = GravityFlags.Center };
            settings.SetTextColor(WinoColors.SecondaryText(context));
            settings.Click += (_, _) => _shell?.OpenSettings();
            _modeCard.AddView(settings, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        }
    }

    /// <summary>Offers only the modes the accounts provide and the user kept (Windows AppModeFooterSwitcherControl).</summary>
    private void ApplyModeVisibility()
    {
        if (_shell is null || View?.Context is not { } context) return;
        var visibility = _shell.ViewModel.ModeVisibility;
        var segments = WinoModeSegments.AppModes.Where(segment => visibility is null || visibility.IsVisible(segment.Mode)).ToArray();
        if (segments.Select(segment => segment.Mode).SequenceEqual(_segments.Select(segment => segment.Mode))) return;
        _segments = segments;
        PopulateModeSwitchers(context);
        ApplyMode();
    }

    private void ApplyMode()
    {
        if (_shell is null || View?.Context is not { } context) return;
        var mode = _shell.ActiveMode ?? WinoApplicationMode.Mail;
        foreach (var bar in new NavigationBarView?[] { _bottomNav, _rail })
            if (bar is not null && bar.SelectedItemId != ItemId(mode)) bar.SelectedItemId = ItemId(mode);
        if (_modeCard is not null)
        {
            for (int index = 0; index < _modeCard.ChildCount; index++)
            {
                if (_modeCard.GetChildAt(index) is not WinoIconView button || button.Tag is null) continue;
                bool active = (int)button.Tag == (int)mode;
                var segment = _segments.FirstOrDefault(candidate => (int)candidate.Mode == (int)button.Tag!);
                if (segment is not null) button.Icon = active ? segment.Filled : segment.Regular;
                button.SetTextColor(active ? WinoColors.Accent(context) : WinoColors.SecondaryText(context));
                button.Selected = active;
            }
        }
        if (View?.FindViewById<FloatingActionButton>(Resource.Id.shell_fab) is { } fab)
            fab.Visibility = mode == WinoApplicationMode.Mail && !_fabSuppressed ? ViewStates.Visible : ViewStates.Gone;
        if (_readerIdle?.FindViewById<WinoIconView>(Resource.Id.shell_reader_idle_glyph) is { } idle)
            idle.Icon = _segments.FirstOrDefault(segment => segment.Mode == mode)?.Filled ?? WinoIconGlyph.MailFilled;
        ApplyStatus();
    }

    private static int ItemId(WinoApplicationMode mode) => (int)mode + 1;

    private WinoApplicationMode? ModeOf(int itemId) => _segments.FirstOrDefault(segment => ItemId(segment.Mode) == itemId)?.Mode;

    // ---- Panes ----

    private void PagesChanged(object? sender, EventArgs args) => PlacePages();

    /// <summary>Shows the navigation service's content page and reader page in this shell's panes. Main thread.</summary>
    private void PlacePages()
    {
        if (_navigation is null || View is null || !IsAdded) return;
        var manager = ChildFragmentManager;
        if (manager.IsDestroyed || manager.IsStateSaved) return;

        Place(manager, Resource.Id.shell_list_host, _navigation.ShellContentEntry ?? null, emptyFallback: true);
        ApplyContentColumns(manager.FindFragmentById(Resource.Id.shell_list_host) as IShellWideContent, _navigation.RenderingEntry is not null);
        if (_readerPane && _navigation.ShowsReaderPane)
        {
            var reader = _navigation.RenderingEntry;
            Place(manager, Resource.Id.shell_reader_host, reader, emptyFallback: false);
            if (_readerIdle is not null) _readerIdle.Visibility = reader is null ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    private int? _listColumnWidth;

    /// <summary>
    /// Medium and expanded: a wide content page (Calendar) fills the list and reader columns and the reader column turns
    /// into its fixed details pane, shown only while details are open; other pages get the list width back.
    /// </summary>
    private void ApplyContentColumns(IShellWideContent? wide, bool hasDetails)
    {
        if (!_readerPane || View is not { } view || view.FindViewById(Resource.Id.shell_list_host)?.Parent is not View listColumn
            || view.FindViewById(Resource.Id.shell_reader_pane) is not { } readerPane
            || listColumn.LayoutParameters is not LinearLayout.LayoutParams listParams || readerPane.LayoutParameters is not LinearLayout.LayoutParams readerParams) return;
        _listColumnWidth ??= listParams.Width;
        if (wide is not null)
        {
            listParams.Width = 0;
            listParams.Weight = 1;
            readerParams.Width = WinoTokens.Px(view.RequireContext(), wide.DetailsPaneWidthResource(WidthClass));
            readerParams.Weight = 0;
            // No event open: the calendar takes the details pane's width too, instead of an empty placeholder.
            readerPane.Visibility = hasDetails ? ViewStates.Visible : ViewStates.Gone;
        }
        else
        {
            listParams.Width = _listColumnWidth.Value;
            listParams.Weight = 0;
            readerParams.Width = 0;
            readerParams.Weight = 1;
            readerPane.Visibility = ViewStates.Visible;
        }
        listColumn.LayoutParameters = listParams;
        readerPane.LayoutParameters = readerParams;
    }

    private void Place(AndroidX.Fragment.App.FragmentManager manager, int containerId, PageEntry? entry, bool emptyFallback)
    {
        var current = manager.FindFragmentById(containerId);
        if (entry is null)
        {
            if (emptyFallback)
            {
                // A mode without a content page yet keeps its placeholder, as on iOS.
                if (current is ModePlaceholderFragment { Entry: null }) return;
                manager.BeginTransaction().SetReorderingAllowed(true).Replace(containerId, new ModePlaceholderFragment()).CommitNowAllowingStateLoss();
            }
            else if (current is not null) manager.BeginTransaction().Remove(current).CommitNowAllowingStateLoss();
            return;
        }
        if (current is WinoFragment shown && ReferenceEquals(shown.Entry, entry)) return;
        manager.BeginTransaction()
            .SetReorderingAllowed(true)
            .SetTransition(current is null ? AndroidX.Fragment.App.FragmentTransaction.TransitNone : AndroidX.Fragment.App.FragmentTransaction.TransitFragmentFade)
            .Replace(containerId, entry.CreateFragment())
            .CommitNowAllowingStateLoss();
    }
}
