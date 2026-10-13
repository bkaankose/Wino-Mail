using System.ComponentModel;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using ObjCRuntime;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Shell.ViewModels;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// The iPad shell, mirroring the Mac shell (macOS design Revision 2, the Mac WinoAppShellViewController): a three-column
/// split view over the theme backdrop. The sidebar (260) shows the mode's menu as Windows rows with the mode switcher card
/// at its foot; the list column (360) hosts the mode's root page and the reader column the rendering frame
/// (<see cref="PadRenderingFrameHost"/>), each in a Wino zone. The toolbar row carries the sidebar toggle, sync and the
/// folder title and subtitle over the list, and the search field, Daily briefing, What's New and the account avatar over
/// the reader. Below 1008pt the sidebar becomes an overlay; in a compact width the columns collapse into one stack.
/// Never the floating top tab bar. Partial files: Toolbar (title, sync, search, briefing, What's New), Modes (per-mode
/// columns, the sidebar content slot, the idle glyph and the narrow toolbar rule).
/// </summary>
public sealed partial class PadShellViewController : UIKitShellViewController
{
    public const double SidebarWidth = 260;
    public const double ListWidth = 360;

    /// <summary>Below this width the sidebar overlays the list instead of tiling beside it (Split View, Stage Manager).</summary>
    public const double SidebarTileMinimumWidth = 1008;

    private readonly PadShellHost _host;
    private readonly IStatePersistanceService _state;
    private readonly UISplitViewController _split = new(UISplitViewControllerStyle.TripleColumn);
    private readonly PadSidebarViewController _sidebar;
    private readonly PadZoneViewController _listRoot = new(WinoIconGlyph.Mail, null);
    private readonly UINavigationController _list;
    private readonly UINavigationController _reader;
    private readonly PadKeyCommands _keys;
    private SplitDelegate? _splitDelegate;
    private IShellCommandTarget? _commandTarget;
    private bool _sidebarHiddenByUser;
    private bool? _tiled;

    public PadShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, PadShellHost host)
        : base(viewModel, navigation, state, services, dispatcher, logger)
    {
        _host = host;
        _state = state;
        ReaderFrame = new PadRenderingFrameHost(host, () => services.GetService<UIKitNavigationService>());
        var menus = new PadSidebarMenuContext(() => MailClient, () => services.GetService<UIKitNavigationService>(), ReportError, FixAccountAsync);
        _sidebar = new PadSidebarViewController(services.GetService<IPictureStorageService>(), dispatcher, ReportError, SelectMenuItem, SwitchMode, FixAccount, menus);
        _list = new UINavigationController(_listRoot);
        _reader = new UINavigationController(ReaderFrame);
        _keys = new PadKeyCommands(this, services.GetService<IKeyboardShortcutService>());
    }

    /// <summary>The reader column.</summary>
    public PadRenderingFrameHost ReaderFrame { get; }

    /// <summary>The content page's command target (the mail list), for the hardware keyboard.</summary>
    public IShellCommandTarget? CommandTarget => _commandTarget;

    /// <summary>The content page's search target.</summary>
    public IShellSearchTarget? SearchTarget => Content as IShellSearchTarget;

    public override void LoadView()
    {
        var root = new UIView();
        WinoLayout.Fill(UIKitThemeService.Track(new ThemeBackdropView()), root);

        _split.PreferredPrimaryColumnWidth = (nfloat)SidebarWidth;
        _split.PreferredSupplementaryColumnWidth = (nfloat)ListWidth;
        _split.MinimumPrimaryColumnWidth = (nfloat)SidebarWidth;
        _split.MinimumSupplementaryColumnWidth = (nfloat)MinimumListWidth;
        _split.MaximumSupplementaryColumnWidth = (nfloat)MaximumListWidth;
        _split.DisplayModeButtonVisibility = UISplitViewControllerDisplayModeButtonVisibility.Never;
        _split.PresentsWithGesture = true;
        _split.ShowsSecondaryOnlyButton = false;
        _split.SetViewController(_sidebar, UISplitViewControllerColumn.Primary);
        _split.SetViewController(_list, UISplitViewControllerColumn.Supplementary);
        _split.SetViewController(_reader, UISplitViewControllerColumn.Secondary);
        _splitDelegate = new SplitDelegate(this);
        _split.Delegate = _splitDelegate;
        foreach (var column in new UINavigationController[] { _list, _reader })
        {
            if (column.View is { } view) view.BackgroundColor = UIColor.Clear;
            ConfigureBar(column.NavigationBar);
        }

        BuildToolbar();
        AddChildViewController(_split);
        if (_split.View is { } splitView)
        {
            splitView.BackgroundColor = UIColor.Clear;
            Layout.Fill(splitView, root);
        }
        _split.DidMoveToParentViewController(this);
        View = root;
        BuildBriefingPanel(root);
        BindShell();
        BindToolbar();
#if DEBUG
        RegisterDebugCommands();
#endif
    }

    /// <summary>A transparent toolbar row: the wallpaper shows through the bar material, as under the Mac unified toolbar.</summary>
    private static void ConfigureBar(UINavigationBar bar)
    {
        var appearance = new UINavigationBarAppearance();
        appearance.ConfigureWithTransparentBackground();
        bar.StandardAppearance = appearance;
        bar.ScrollEdgeAppearance = appearance;
        bar.CompactAppearance = appearance;
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (View is { } view) ApplyWidth(view.Bounds.Width);
        ApplyColumns();
        LayoutBriefingPanel();
    }

    public override void ViewWillTransitionToSize(CGSize toSize, IUIViewControllerTransitionCoordinator coordinator)
    {
        base.ViewWillTransitionToSize(toSize, coordinator);
        ApplyWidth(toSize.Width);
        coordinator.AnimateAlongsideTransition(_ => ApplyColumns(), _ => ApplyColumns());
    }

    /// <summary>Tiles the sidebar at 1008pt and wider; narrower windows show it as an overlay over the list.</summary>
    private void ApplyWidth(nfloat width)
    {
        bool tiled = width >= SidebarTileMinimumWidth;
        if (_tiled == tiled) return;
        _tiled = tiled;
        _split.PreferredSplitBehavior = tiled ? UISplitViewControllerSplitBehavior.Tile : UISplitViewControllerSplitBehavior.Overlay;
        _split.PreferredDisplayMode = tiled && !_sidebarHiddenByUser
            ? UISplitViewControllerDisplayMode.TwoBesideSecondary
            : UISplitViewControllerDisplayMode.OneBesideSecondary;
    }

    public void ToggleSidebar()
    {
        bool visible = _split.DisplayMode is UISplitViewControllerDisplayMode.TwoBesideSecondary or UISplitViewControllerDisplayMode.TwoOverSecondary
            or UISplitViewControllerDisplayMode.TwoDisplaceSecondary;
        if (_tiled == true)
        {
            _sidebarHiddenByUser = visible;
            if (visible) _split.HideColumn(UISplitViewControllerColumn.Primary);
            else _split.ShowColumn(UISplitViewControllerColumn.Primary);
            return;
        }
        // Narrow windows: the sidebar slides over the list and reader instead of pushing them aside.
        _split.PreferredDisplayMode = visible ? UISplitViewControllerDisplayMode.OneBesideSecondary : UISplitViewControllerDisplayMode.TwoOverSecondary;
    }

    public override UINavigationController? CurrentNavigationController => _list;

    protected override void PlaceContent(UIViewController? controller)
    {
        DetachCommandTarget();
        ResetSearch();
        if (controller is null)
        {
            _list.SetViewControllers([_listRoot], false);
            return;
        }
        controller.NavigationItem.LeftBarButtonItems = _listRoot.NavigationItem.LeftBarButtonItems;
        controller.NavigationItem.TitleView = _titleView;
        controller.NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _list.SetViewControllers([controller], false);
        AttachCommandTarget(controller as IShellCommandTarget);
        UpdateSearchAvailability();
    }

    /// <summary>The reader column (the Mac IRenderingFrameHost): the page replaces the frame's page.</summary>
    public bool ShowInReader(UIViewController controller)
    {
        _ = View;
        ReaderFrame.Show(controller);
        _split.ShowColumn(UISplitViewControllerColumn.Secondary);
        return true;
    }

    /// <summary>True when <paramref name="responder"/> sits in the reader column.</summary>
    internal bool IsInReaderFrame(UIResponder? responder)
        => responder is UIView view && ReaderFrame.ViewIfLoaded is { } frame && view.IsDescendantOfView(frame);

    protected override void ModeChanged(WinoApplicationMode mode)
    {
        _sidebar.SetMode(mode);
        ApplyModePolicy(mode);
        _listRoot.Title = ModeTitle(mode);
        if (ReaderFrame.Page is not null) _ = ClearReaderAsync();
        UpdateTitle();
    }

    private async Task ClearReaderAsync()
    {
        try { await ReaderFrame.ClearAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void MenuChanged(ShellMenu? menu) => _sidebar.Bind(menu);

    protected override void SelectedItemChanged(object? item)
    {
        _sidebar.SetSelectedItem(item as IMenuItem);
        ObserveTitle(item as INotifyPropertyChanged);
        UpdateTitle();
    }

    /// <summary>File › New: the current mode's "new item" pane entry (new mail, event, contact or list).</summary>
    public void NewItem()
    {
        if (ViewModel.CurrentProvider is IMailShellClient mail) { SelectMenuItem(mail.CreatePrimaryMenuItem); return; }
        var item = ViewModel.CurrentMenu?.Items.FirstOrDefault(entry => entry is NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem);
        if (item is not null) SelectMenuItem(item);
    }

    /// <summary>
    /// Windows KeyboardShortcutController.DispatchAsync for the root page: the content page's ViewModel handles the
    /// shortcut first, then the mode's provider.
    /// </summary>
    public async Task RouteKeyboardShortcutAsync(KeyboardShortcutTriggerDetails details)
    {
        if (ContentViewModel is Wino.Core.ViewModels.CoreBaseViewModel page) await page.KeyboardShortcutHook(details);
        if (!details.Handled) await ViewModel.KeyboardShortcutHookForMode(details);
    }

    private void SwitchMode(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings)
        {
            OpenSettings();
            if (ActiveMode is { } current) _sidebar.SetMode(current);
            return;
        }
        RequestMode(mode);
    }

    private async void FixAccount(IMenuItem item)
    {
        try { await FixAccountAsync(item); }
        catch (Exception exception) { ReportError(exception); }
    }

    private Task FixAccountAsync(IMenuItem item)
        => MailClient is { } mail && item is IAccountNavigationMenuItem { Account: { } account }
            ? mail.HandleAccountAttentionAsync(account)
            : ViewModel.InvokeMenuItemAsync(item);

    #region Command target

    private void AttachCommandTarget(IShellCommandTarget? target)
    {
        _commandTarget = target;
        if (target is not null) target.CommandStateChanged += TargetStateChanged;
    }

    private void DetachCommandTarget()
    {
        if (_commandTarget is not null) _commandTarget.CommandStateChanged -= TargetStateChanged;
        _commandTarget = null;
    }

    // Key commands are rebuilt on demand; a selection change only needs the menu-bar discoverability refreshed.
    private void TargetStateChanged(object? sender, EventArgs args) => UIMenuSystem.MainSystem.SetNeedsRebuild();

    #endregion

    #region Hardware keyboard

    public override bool CanBecomeFirstResponder => true;

    public override UIKeyCommand[] KeyCommands => _keys.Build();

    [Export(PadKeyCommands.SelectorName)]
    public void PadKeyCommand(UIKeyCommand command) => _keys.Invoke(command);

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        BecomeFirstResponder();
    }

    #endregion

    public override async Task ReleaseAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            DetachCommandTarget();
            ReleaseToolbar();
        });
        await ClearReaderAsync();
        await base.ReleaseAsync();
    }

    /// <summary>
    /// Picks the column that stays on top when the split collapses to one stack (compact width): the reader while a page
    /// is open, else the list.
    /// </summary>
    private sealed class SplitDelegate(PadShellViewController owner) : UISplitViewControllerDelegate
    {
        public override UISplitViewControllerColumn GetTopColumnForCollapsing(UISplitViewController splitViewController, UISplitViewControllerColumn proposedTopColumn)
            => owner.ReaderFrame.Page is not null ? UISplitViewControllerColumn.Secondary : UISplitViewControllerColumn.Supplementary;
    }

#if DEBUG
    /// <summary>Debug bridge: "sidebar" toggles the sidebar; "padinfo" reports the split state and the content page.</summary>
    private void RegisterDebugCommands()
    {
        UIKitDebugBridge.Register("sidebar", _ =>
        {
            ToggleSidebar();
            return Task.FromResult("ok " + _split.DisplayMode);
        });
        UIKitDebugBridge.Register("padcolumns", _ => Task.FromResult(DescribeColumns()));
        UIKitDebugBridge.Register("padinfo", _ => Task.FromResult(
            $"mode={ActiveMode} display={_split.DisplayMode} behavior={_split.SplitBehavior} collapsed={_split.Collapsed} " +
            $"content={Content?.GetType().Name} reader={ReaderFrame.Page?.GetType().Name} title='{_titleLabel.Text}' subtitle='{_subtitleLabel.Text}' " +
            $"keys={string.Join(",", KeyCommands.Select(command => command.Title))}"));
        UIKitDebugBridge.Register("search", args =>
        {
            FocusSearch();
            var text = string.Join(' ', args);
            _search.Text = text;
            SearchTextChanged(text);
            return Task.FromResult("ok");
        });
        // "orient landscape|portrait" rotates the scene; "dismiss" closes whatever is presented over the shell.
        UIKitDebugBridge.Register("orient", args =>
        {
            if (View?.Window?.WindowScene is not { } scene) return Task.FromResult("no scene");
            var mask = args.Length > 0 && args[0].StartsWith("l", StringComparison.OrdinalIgnoreCase)
                ? UIInterfaceOrientationMask.LandscapeLeft
                : UIInterfaceOrientationMask.Portrait;
            scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(mask), error => Serilog.Log.Warning("orient: {Error}", error?.LocalizedDescription));
            return Task.FromResult("ok " + mask);
        });
        UIKitDebugBridge.Register("dismiss", _ =>
        {
            if (PresentedViewController is not { } presented) return Task.FromResult("nothing presented");
            presented.DismissViewController(false, null);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("padbriefing", _ =>
        {
            ToggleBriefing();
            return Task.FromResult(_briefingOpen ? "open" : "closed");
        });
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachCommandTarget();
            _sidebar.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A column page before its content exists: an empty Wino zone with the mode glyph.</summary>
internal sealed class PadZoneViewController(WinoIconGlyph glyph, string? caption) : UIViewController
{
    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView { CornerRadius = PadRenderingFrameHost.ZoneRadius };
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, 0),
            zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        var state = new Wino.Mail.Controls.UIKit.Extras.WinoStateView(glyph, string.Empty, caption, WinoStyle.TertiaryText);
        state.Place(zone.ContentView);
        View = root;
    }
}
