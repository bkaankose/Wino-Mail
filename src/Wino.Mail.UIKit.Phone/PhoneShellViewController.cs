using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Mail.UIKit.Phone.Calendar;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Phone.ToDo;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;
using Wino.Shell.ViewModels;

namespace Wino.Mail.UIKit.Phone;

/// <summary>
/// The iPhone shell (iOS design decisions, revised 18 Oct): a tab bar with Mail, Calendar, Contacts and To Do, each a
/// navigation stack with compact bars (never large titles) and a pinned search bar, over the theme backdrop. Tabs switch
/// modes through the shared shell ViewModel. Every tab's root page has the hamburger button leading, which opens the
/// shell drawer (<see cref="ShellDrawer"/>, also a left-edge pan) with the active mode's menu: Mailboxes, the calendars,
/// the contact filters or the To Do lists, each with the Settings row at its foot. Settings is never a tab and opens as a
/// full-screen sheet. The mode's content page is its stack's root. The shell also owns the Mail stack's toolbar: the
/// reader toolbar while the reader is on top, the list's edit toolbar in edit mode, hidden otherwise.
/// </summary>
public sealed class PhoneShellViewController : UIKitShellViewController
{
    private readonly UITabBarController _tabs = new();
    private readonly UINavigationController[] _stacks;
    private readonly UIViewController[] _roots;
    private readonly MailboxesViewController _mailboxes;
    private readonly ShellDrawer _drawer;
    private readonly CalendarDrawerViewController _calendarDrawer;
    private readonly MailRootViewController _mailRoot;
    private readonly ModeMenuRootViewController[] _modeRoots;
    private readonly IReadOnlyList<WinoModeSegment> _segments = WinoModeSegments.AppModes;
    private readonly StackDelegate _stackDelegate;
    private readonly List<(WeakReference<UIViewController> Reader, MailReaderToolbar Toolbar)> _readerToolbars = [];
    private readonly List<WeakReference<ShellAvatarButton>> _avatars = [];
    private readonly ShellFloatingButton _floatingButton = new() { Hidden = true, Alpha = 0 };
    private bool _floatingButtonVisible;
    private readonly IPictureStorageService? _pictures;
    private List<MailAccount> _accounts = [];
    private IReadOnlyList<MailAccount> _currentAccounts = [];

    public PhoneShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, navigation, state, services, dispatcher, logger)
    {
        _pictures = services.GetService<IPictureStorageService>();
        _mailboxes = new MailboxesViewController(this, services);
        _drawer = new ShellDrawer(this, _mailboxes, CanOpenDrawer);
        _calendarDrawer = new CalendarDrawerViewController(services.GetRequiredService<IAccountCalendarStateService>(), OpenSettingsFromDrawer);
        _mailRoot = new MailRootViewController(this);
        var contactsTitle = _segments.FirstOrDefault(segment => segment.Mode == WinoApplicationMode.Contacts)?.Title ?? string.Empty;
        _modeRoots =
        [
            new ModeMenuRootViewController(this, WinoApplicationMode.Tasks, dispatcher, new ToDoMenuRowStyle()),
            new ModeMenuRootViewController(this, WinoApplicationMode.Contacts, dispatcher, new PresentationMenuRowStyle(contactsTitle, services.GetService<IPictureStorageService>()))
        ];
        _stackDelegate = new StackDelegate(this);
        _roots = _segments.Select(segment => segment.Mode == WinoApplicationMode.Mail
            ? _mailRoot
            : (UIViewController)new ModePlaceholderViewController(segment)).ToArray();
        _stacks = _roots.Select((root, index) => CreateStack(root, _segments[index])).ToArray();
    }

    private UINavigationController CreateStack(UIViewController root, WinoModeSegment segment)
    {
        // The Mail root brings its own bar items.
        if (root is not MailRootViewController) root.NavigationItem.LeftBarButtonItem = MenuButton();
        var stack = new UINavigationController(root) { Delegate = _stackDelegate };
        // Compact 44pt bars on every stack: large titles cost a whole row of the screen.
        stack.NavigationBar.PrefersLargeTitles = false;
        // Bars blur the wallpaper behind them (iOS design decisions: wallpaper backdrop, blurred bars).
        var toolbar = new UIToolbarAppearance();
        toolbar.ConfigureWithDefaultBackground();
        stack.Toolbar.StandardAppearance = toolbar;
        stack.Toolbar.ScrollEdgeAppearance = toolbar;
        stack.TabBarItem = new UITabBarItem(segment.Title, TabImage(segment.Regular), TabImage(segment.Filled));
        if (stack.View is { } view) view.BackgroundColor = UIColor.Clear;
        return stack;
    }

    private static UIImage TabImage(WinoIconGlyph glyph)
        => WinoIcons.TemplateImage(glyph, 26);

    /// <summary>
    /// The avatar button that leads every tab's root bar (approach A of 18 Oct 2026): the current account, which opens the
    /// shell drawer.
    /// </summary>
    internal UIBarButtonItem MenuButton()
    {
        var button = new ShellAvatarButton(OpenDrawer);
        _avatars.RemoveAll(static reference => !reference.TryGetTarget(out _));
        _avatars.Add(new WeakReference<ShellAvatarButton>(button));
        ApplyAvatar(button);
        if (_accounts.Count == 0) _ = LoadAccountsAsync();
        return new UIBarButtonItem(button);
    }

    /// <summary>The account of the selected mail folder (every account of a unified inbox); the first account until then.</summary>
    private void UpdateCurrentAccounts(object? item)
    {
        IReadOnlyList<MailAccount>? accounts = item switch
        {
            IFolderMenuItem folder => [folder.ParentAccount],
            AccountMenuItem account => [account.Account],
            MergedAccountFolderMenuItem merged => merged.Parameter.Select(static folder => folder.MailAccountId).Distinct()
                .Select(id => _accounts.FirstOrDefault(account => account.Id == id)).OfType<MailAccount>().ToList(),
            _ => null
        };
        if (accounts is not { Count: > 0 }) return;
        _currentAccounts = accounts;
        foreach (var reference in _avatars)
            if (reference.TryGetTarget(out var button)) ApplyAvatar(button);
    }

    private void ApplyAvatar(ShellAvatarButton button)
    {
        var accounts = _currentAccounts.Count > 0 ? _currentAccounts : _accounts.Take(1).ToList();
        var icons = accounts.Take(2).Select(account => (IAccountIconInfo)(_pictures is null
            ? MailAccountIconInfoFactory.CreateProviderFallback(account.ProviderType, account.SpecialImapProvider)
            : MailAccountIconInfoFactory.Create(account, _pictures))).ToList();
        button.SetAccounts(icons, string.Join(", ", accounts.Select(static account => account.Name)));
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            if (Services.GetService<IAccountService>() is not { } service) return;
            var accounts = (await service.GetAccountsAsync()).OrderBy(static account => account.Order).ToList();
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _accounts = accounts;
                UpdateCurrentAccounts(SelectedMenuItem);
                foreach (var reference in _avatars)
                    if (reference.TryGetTarget(out var button)) ApplyAvatar(button);
            });
        }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The bar avatar could not load the accounts."); }
    }

    /// <summary>The drawer content of <paramref name="mode"/>.</summary>
    private UIViewController DrawerRoot(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => _calendarDrawer,
        _ => _modeRoots.FirstOrDefault(root => root.Mode == mode) ?? (UIViewController)_mailboxes
    };

    /// <summary>The phone shell that hosts <paramref name="controller"/>, if any.</summary>
    internal static PhoneShellViewController? Of(UIViewController controller)
    {
        for (var current = controller.ParentViewController; current is not null; current = current.ParentViewController)
            if (current is PhoneShellViewController shell) return shell;
        return null;
    }

    /// <summary>
    /// Loads and lays out a page at <paramref name="stack"/>'s size before it is placed or pushed, so an animated
    /// transition never shows its first layout growing from a zero frame (the page "drawing" from the top-left).
    /// </summary>
    internal static void PrepareForTransition(UIViewController controller, UINavigationController stack)
    {
        controller.LoadViewIfNeeded();
        if (controller.View is not { } view || stack.View is not { } host) return;
        UIView.PerformWithoutAnimation(() =>
        {
            if (view.Superview is null) view.Frame = host.Bounds;
            view.SetNeedsLayout();
            view.LayoutIfNeeded();
        });
    }

    public override void LoadView()
    {
        var root = new UIView();
        WinoLayout.Fill(UIKitThemeService.Track(new ThemeBackdropView()), root);
        _tabs.ViewControllers = _stacks;
        _tabs.Delegate = new TabSelection(this);
        AddChildViewController(_tabs);
        if (_tabs.View is { } tabsView)
        {
            tabsView.BackgroundColor = UIColor.Clear;
            Layout.Fill(tabsView, root);
        }
        _tabs.DidMoveToParentViewController(this);
        _floatingButton.PrimaryActionTriggered += (_, _) => FloatingActionInvoked();
        root.AddSubview(_floatingButton);
        _drawer.Attach(root);
        View = root;
        BindShell();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (View is { } root)
        {
            LayoutFloatingButton(root);
            _drawer.Layout(root.Bounds);
        }
    }

    public override UINavigationController? CurrentNavigationController
        => _tabs.SelectedViewController as UINavigationController ?? _stacks[IndexOf(ActiveMode ?? WinoApplicationMode.Mail)];

    /// <summary>The tab bar shows the visible modes only; the stacks keep every mode so a mode shown again keeps its pages.</summary>
    protected override void ModeVisibilityChanged(AppModeVisibility? visibility)
    {
        var visible = _stacks.Where((_, index) => visibility is null || visibility.IsVisible(_segments[index].Mode)).ToArray();
        var current = _tabs.ViewControllers ?? [];
        if (!visible.SequenceEqual(current)) _tabs.ViewControllers = visible;
        if (ActiveMode is { } mode and not WinoApplicationMode.Settings) SelectTab(mode);
    }

    private void SelectTab(WinoApplicationMode mode)
    {
        var tab = Array.IndexOf(_tabs.ViewControllers ?? [], _stacks[IndexOf(mode)]);
        if (tab >= 0 && _tabs.SelectedIndex != tab) _tabs.SelectedIndex = tab;
    }

    private UINavigationController MailStack => _stacks[IndexOf(WinoApplicationMode.Mail)];

    private int IndexOf(WinoApplicationMode mode)
    {
        for (int index = 0; index < _segments.Count; index++)
            if (_segments[index].Mode == mode) return index;
        return 0;
    }

    protected override void PlaceContent(UIViewController? controller)
    {
        var index = IndexOf(ActiveMode ?? WinoApplicationMode.Mail);
        var stack = _stacks[index];
        var root = _roots[index];
        if (controller is null) stack.SetViewControllers([root], false);
        else if (root is MailRootViewController)
        {
            // The message list is the Mail stack's root; the drawer closes over it.
            PrepareForTransition(controller, stack);
            if (controller is MailListPageViewController { HasMenuItem: false } list) list.SetMenuItem(MenuButton());
            if (stack.ViewControllers is not [var placed] || !ReferenceEquals(placed, controller)) stack.SetViewControllers([controller], false);
        }
        else
        {
            PrepareForTransition(controller, stack);
            // After the page loaded its own bar items: the account avatar always leads a tab's root page. A page placed again
            // (a calendar date change routes through the shell) keeps its avatar and bar, so nothing re-animates.
            if (controller.NavigationItem.LeftBarButtonItem?.CustomView is not ShellAvatarButton) controller.NavigationItem.LeftBarButtonItem = MenuButton();
            if (stack.ViewControllers is not [var placed] || !ReferenceEquals(placed, controller)) stack.SetViewControllers([controller], false);
        }
        UpdateFloatingButton();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        UpdateFloatingButton();
    }

    protected override void ModeChanged(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings) return;
        _drawer.SetRoot(DrawerRoot(mode));
        UpdateFloatingButton();
        SelectTab(mode);
    }

    protected override void MenuChanged(ShellMenu? menu)
    {
        _mailboxes.SetMenu(ActiveMode == WinoApplicationMode.Mail ? menu : null);
        foreach (var root in _modeRoots) root.SetMenu(ActiveMode == root.Mode ? menu : null);
    }

    protected override void SelectedItemChanged(object? item)
    {
        if (ActiveMode == WinoApplicationMode.Mail)
        {
            UpdateCurrentAccounts(item);
            // A unified inbox needs every account; a new account shows up in the next selection.
            if (item is MergedAccountFolderMenuItem || _accounts.Count == 0) _ = LoadAccountsAsync();
        }
        _mailboxes.SetSelected(ActiveMode == WinoApplicationMode.Mail ? item : null);
        foreach (var root in _modeRoots) root.SetSelected(ActiveMode == root.Mode ? item : null);
    }

    // ---- The floating action button ----

    /// <summary>Bottom right, 16pt above the floating tab bar and 16pt from the edge.</summary>
    private void LayoutFloatingButton(UIView root)
    {
        var size = ShellFloatingButton.Diameter;
        var tabBar = _tabs.TabBar;
        var top = tabBar.Hidden || tabBar.Window is null
            ? (double)(root.Bounds.Height - root.SafeAreaInsets.Bottom)
            : (double)root.ConvertRectFromView(tabBar.Bounds, tabBar).Top;
        _floatingButton.Frame = new CGRect((double)(root.Bounds.Width - root.SafeAreaInsets.Right) - 16 - size, top - 16 - size, size, size);
    }

    /// <summary>
    /// Shows the button with the active mode's create glyph while the tab's root page is on top, hides it over pushed
    /// pages, sheets and the message list's edit mode.
    /// </summary>
    internal void UpdateFloatingButton()
    {
        if (ActiveMode is not { } mode || mode == WinoApplicationMode.Settings)
        {
            _floatingButtonVisible = false;
            _floatingButton.Hidden = true;
            return;
        }
        var stack = _stacks[IndexOf(mode)];
        bool visible = (stack.ViewControllers?.Length ?? 0) == 1
            && stack.TopViewController is not MailListPageViewController { IsInSelectionMode: true }
            && stack.TopViewController is not ModePlaceholderViewController;
        var (glyph, label) = mode switch
        {
            WinoApplicationMode.Calendar => (WinoIconGlyph.Add, Translator.CalendarEventCompose_NewEventButton),
            WinoApplicationMode.Contacts => (WinoIconGlyph.PersonAdd, Translator.ContactsPane_NewContact),
            WinoApplicationMode.Tasks => (WinoIconGlyph.Add, Translator.ToDoPage_AddTask),
            _ => (WinoIconGlyph.NewMail, Translator.MenuNewMail)
        };
        _floatingButton.Set(glyph, label);
        if (_floatingButtonVisible == visible) return;
        _floatingButtonVisible = visible;
        _floatingButton.Hidden = false;
        if (View is { } root) root.BringSubviewToFront(_floatingButton);
        UIView.Animate(0.2, 0, UIViewAnimationOptions.BeginFromCurrentState, () =>
        {
            _floatingButton.Alpha = visible ? 1 : 0;
            _floatingButton.Transform = visible ? CGAffineTransform.MakeIdentity() : CGAffineTransform.MakeScale(0.6f, 0.6f);
        }, () => _floatingButton.Hidden = !_floatingButtonVisible);
    }

    private void FloatingActionInvoked()
    {
        if (ActiveMode is not { } mode) return;
        if (_stacks[IndexOf(mode)].TopViewController is IPhoneFloatingActionPage page) page.FloatingActionInvoked();
        else if (mode == WinoApplicationMode.Mail) Compose();
    }

    // ---- The shell drawer ----

    /// <summary>
    /// A row in the drawer of Contacts or To Do: the drawer closes and the shell selects the item (a filter, list or smart
    /// view the content page then shows).
    /// </summary>
    internal void OpenFromDrawer(IMenuItem item)
    {
        if (Wino.Mail.ViewModels.Data.ShellMenuItemRules.SelectsOnInvoked(item)) _drawer.Close(true);
        SelectMenuItem(item);
    }

    /// <summary>Opens the shell drawer (the hamburger button, the left-edge pan).</summary>
    internal void OpenDrawer()
    {
        if (CanOpenDrawer()) _drawer.Open(true);
    }

    /// <summary>Closes the shell drawer.</summary>
    internal void CloseDrawer(bool animated = true) => _drawer.Close(animated);

    /// <summary>
    /// The drawer opens over a tab's root page only: not over a pushed page, a sheet, or the message list in edit mode.
    /// </summary>
    private bool CanOpenDrawer()
    {
        if (ActiveMode is not { } mode || mode == WinoApplicationMode.Settings || !ReferenceEquals(_tabs.SelectedViewController, _stacks[IndexOf(mode)]) || PresentedViewController is not null) return false;
        var stack = _stacks[IndexOf(mode)];
        if ((stack.ViewControllers?.Length ?? 0) != 1) return false;
        return stack.TopViewController is not MailListPageViewController { IsInSelectionMode: true };
    }

    /// <summary>The drawer's Settings row: Settings opens once the drawer has closed.</summary>
    internal void OpenSettingsFromDrawer() => _drawer.Close(true, OpenSettings);

    // ---- Mail flow hooks (Mailboxes, the message list and the reader) ----

    /// <summary>The Mail provider while Mail is the active mode (synchronization status for Mailboxes).</summary>
    internal IShellMenuProvider? MailProvider => ActiveMode == WinoApplicationMode.Mail ? ViewModel.CurrentProvider : null;

    /// <summary>The shell's selected menu item.</summary>
    internal object? SelectedMenuItem => ViewModel.SelectedMenuItem;

    /// <summary>A Mailboxes row tap: the drawer closes and the shell lists the folder under it.</summary>
    internal void OpenFromMailboxes(IMenuItem item)
    {
        _drawer.Close(true);
        SelectMenuItem(item);
    }

    /// <summary>
    /// Closes the drawer over the folder list when it is still the shell's live content (the folder already listed), and
    /// brings the list back from under a reader. False when there is nothing to show again.
    /// </summary>
    internal bool ShowRetainedMailContent()
    {
        if (ActiveMode != WinoApplicationMode.Mail || Content is not MailListPageViewController { IsRetained: true } list) return false;
        var stack = MailStack;
        if (Array.IndexOf(stack.ViewControllers ?? [], list) < 0) return false;
        _drawer.Close(true);
        if (!ReferenceEquals(stack.TopViewController, list)) stack.PopToViewController(list, false);
        return true;
    }

    /// <summary>The New Message button: the shared Mail provider creates the draft, which opens in the composer sheet.</summary>
    internal void Compose()
    {
        _drawer.Close(true);
        if (MailClient is { } mail) SelectMenuItem(mail.CreatePrimaryMenuItem);
    }

    /// <summary>Presents the daily briefing as a sheet (medium and large detents) through the shared briefing presenter.</summary>
    internal void ShowBriefing() => Services.GetService<IUIKitDailyBriefingPresenter>()?.PresentSheet();

    /// <summary>Gives the reader page its bottom toolbar; it is released when the reader leaves the stack.</summary>
    internal void AttachReaderToolbar(UIViewController reader, IMailReaderCommands commands)
    {
        var toolbar = new MailReaderToolbar(commands);
        reader.ToolbarItems = toolbar.Items;
        _readerToolbars.Add((new WeakReference<UIViewController>(reader), toolbar));
    }

    /// <summary>The reader toolbar's Move: the Move sheet of the folder list for <paramref name="mails"/>.</summary>
    internal bool TryShowMove(IReadOnlyList<Wino.Core.Domain.Entities.Mail.MailCopy> mails)
        => Content is MailListPageViewController list && list.TryShowMove(mails);

#if DEBUG
    /// <summary>Debug: the attached reader toolbar's buttons with their enabled state.</summary>
    internal string DebugReaderToolbar()
        => string.Join(" | ", _readerToolbars.Select(static entry => string.Join(",", entry.Toolbar.Buttons.Select(static button => $"{button.AccessibilityLabel}={(button.Enabled ? "on" : "off")}"))));

    /// <summary>Debug: taps reader toolbar button <paramref name="index"/> (0 Archive … 5 More) as a touch does.</summary>
    internal string DebugTapReaderButton(int index)
    {
        if (_readerToolbars.Count == 0) return "no reader toolbar";
        var buttons = _readerToolbars[^1].Toolbar.Buttons;
        if (index < 0 || index >= buttons.Length) return "no button " + index;
        buttons[index].SendActionForControlEvents(UIControlEvent.PrimaryActionTriggered);
        return "tapped " + buttons[index].AccessibilityLabel;
    }
#endif

    /// <summary>Disposes reader toolbars whose reader is no longer on a stack.</summary>
    private void PruneReaderToolbars()
    {
        for (int index = _readerToolbars.Count - 1; index >= 0; index--)
        {
            var (reference, toolbar) = _readerToolbars[index];
            if (reference.TryGetTarget(out var reader) && _stacks.Any(stack => Array.IndexOf(stack.ViewControllers ?? [], reader) >= 0)) continue;
            toolbar.Dispose();
            if (reference.TryGetTarget(out var gone)) gone.ToolbarItems = [];
            _readerToolbars.RemoveAt(index);
        }
    }

    /// <summary>Shows the stack's toolbar only for pages that bring items (the reader, the list in edit mode).</summary>
    private sealed class StackDelegate(PhoneShellViewController shell) : UINavigationControllerDelegate
    {
        public override void WillShowViewController(UINavigationController navigationController, UIViewController viewController, bool animated)
        {
            navigationController.SetToolbarHidden(viewController.ToolbarItems is not { Length: > 0 }, animated);
            // A push hides the button at once; the root coming back shows it once it is on screen.
            if (navigationController.ViewControllers?.Length > 1) shell.UpdateFloatingButton();
        }

        public override void DidShowViewController(UINavigationController navigationController, UIViewController viewController, bool animated)
        {
            shell.PruneReaderToolbars();
            shell.UpdateFloatingButton();
        }
    }

    /// <summary>A tab tap asks the router for its mode; the shell then activates it like the Windows shell.</summary>
    private sealed class TabSelection(PhoneShellViewController shell) : UITabBarControllerDelegate
    {
        public override bool ShouldSelectViewController(UITabBarController tabBarController, UIViewController viewController)
        {
            var index = Array.IndexOf(shell._stacks, viewController);
            if (index >= 0 && shell._segments[index].Mode != shell.ActiveMode) shell.RequestMode(shell._segments[index].Mode);
            return true;
        }
    }

#if DEBUG
    internal string DebugDescribeMailboxes() => _mailboxes.DebugDescribe();

    /// <summary>Debug: the active mode's menu root rows, or a row tap ("moderoot", "moderoot-tap N").</summary>
    internal string DebugModeRoot(int? tap)
    {
        var root = _modeRoots.FirstOrDefault(candidate => candidate.Mode == ActiveMode);
        if (root is null) return "no mode root for " + ActiveMode;
        return tap is { } index ? root.DebugTap(index) : root.DebugDescribe();
    }

    internal string DebugOpenFolder(string name) => _mailboxes.DebugOpen(name);

    /// <summary>Debug: "drawer open|close" or the drawer state.</summary>
    internal string DebugDrawer(string? action)
    {
        switch (action)
        {
            case "open":
                if (!CanOpenDrawer()) return "cannot open";
                _drawer.Open(true);
                return "ok";
            case "close":
                _drawer.Close(true);
                return "ok";
            default:
                return $"open={_drawer.IsOpen} canOpen={CanOpenDrawer()}";
        }
    }
#endif

    public override async Task ReleaseAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var (_, toolbar) in _readerToolbars) toolbar.Dispose();
            _readerToolbars.Clear();
        });
        await base.ReleaseAsync();
    }
}

/// <summary>
/// The Mail stack's root while no folder is listed (no account yet, or before the first folder loads): an empty zone with
/// the hamburger button that opens Mailboxes and the New Message button, like the message list's bar.
/// </summary>
internal sealed class MailRootViewController : UIViewController
{
    private readonly PhoneShellViewController _shell;

    public MailRootViewController(PhoneShellViewController shell)
    {
        _shell = shell;
        NavigationItem.Title = Translator.IOS_MailboxesTitle;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        NavigationItem.LeftBarButtonItem = shell.MenuButton();
        NavigationItem.RightBarButtonItem = PhoneBarItems.Compose(shell.Compose);
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView();
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 8),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
            zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        new WinoStateView(WinoIconGlyph.Mail, Translator.IOS_MailboxesTitle, null).Place(zone.ContentView);
        View = root;
    }
}

/// <summary>A mode's stack root before its page is built: the mode glyph and title in a Wino zone.</summary>
internal sealed class ModePlaceholderViewController : UIViewController
{
    private readonly WinoModeSegment _segment;

    public ModePlaceholderViewController(WinoModeSegment segment)
    {
        _segment = segment;
        Title = segment.Title;
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView();
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, (nfloat)WinoStyle.ZoneGutter),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
            zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        new WinoStateView(_segment.Regular, _segment.Title, Translator.Info_UnsupportedFunctionalityDescription).Place(zone.ContentView);
        View = root;
    }
}
