using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Phone.ToDo;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.ViewModels;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;
using Wino.Shell.ViewModels;

namespace Wino.Mail.UIKit.Phone;

/// <summary>
/// The iPhone shell (iOS design decisions, revised 15 Oct): a tab bar with Mail, Calendar, Contacts and To Do, each a
/// navigation stack with compact bars (never large titles), over the theme backdrop. Tabs switch modes through the shared
/// shell ViewModel; Settings is never a tab: the Settings row at the foot of the Mailboxes drawer and the gear button of
/// the other modes open it as a full-screen sheet. The Mail stack's root is the message list itself; Mailboxes is a
/// drawer over it (<see cref="MailboxesDrawer"/>) opened from the list's sidebar button or a left-edge pan. To Do keeps
/// its shell-owned Lists root under its content (<see cref="ModeMenuRootViewController"/>); Calendar and Contacts content
/// is the stack's root. The shell also owns the Mail stack's toolbar: the reader toolbar while the reader is on top, the
/// list's edit toolbar in edit mode, hidden otherwise.
/// </summary>
public sealed class PhoneShellViewController : UIKitShellViewController
{
    private readonly UITabBarController _tabs = new();
    private readonly UINavigationController[] _stacks;
    private readonly UIViewController[] _roots;
    private readonly MailboxesViewController _mailboxes;
    private readonly MailboxesDrawer _drawer;
    private readonly MailRootViewController _mailRoot;
    private readonly ModeMenuRootViewController[] _modeRoots;
    private readonly IReadOnlyList<WinoModeSegment> _segments = WinoModeSegments.AppModes;
    private readonly StackDelegate _stackDelegate;
    private readonly List<(WeakReference<UIViewController> Reader, MailReaderToolbar Toolbar)> _readerToolbars = [];
    private bool _animateNextContent;

    public PhoneShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, navigation, state, services, dispatcher, logger)
    {
        _mailboxes = new MailboxesViewController(this, services);
        _drawer = new MailboxesDrawer(this, _mailboxes, CanOpenMailboxes);
        _mailRoot = new MailRootViewController(this);
        _modeRoots = [new ModeMenuRootViewController(this, WinoApplicationMode.Tasks, dispatcher, new ToDoMenuRowStyle())];
        _stackDelegate = new StackDelegate(this);
        _roots = _segments.Select(segment => segment.Mode == WinoApplicationMode.Mail
            ? _mailRoot
            : _modeRoots.FirstOrDefault(root => root.Mode == segment.Mode) ?? (UIViewController)new ModePlaceholderViewController(segment)).ToArray();
        _stacks = _roots.Select((root, index) => CreateStack(root, _segments[index])).ToArray();
    }

    private UINavigationController CreateStack(UIViewController root, WinoModeSegment segment)
    {
        // The Mail root brings its own bar items (the drawer holds Settings in Mail).
        if (root is not MailRootViewController) root.NavigationItem.RightBarButtonItem = SettingsButton();
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

    /// <summary>The gear button of the Calendar, Contacts and To Do bars: Settings as a full-screen sheet.</summary>
    private UIBarButtonItem SettingsButton() => PhoneBarItems.Button(WinoIconGlyph.Settings, Translator.MenuSettings, OpenSettings);

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
        _drawer.Attach(root);
        View = root;
        BindShell();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (View is { } root) _drawer.Layout(root.Bounds);
    }

    public override UINavigationController? CurrentNavigationController => _stacks[Math.Clamp((int)_tabs.SelectedIndex, 0, _stacks.Length - 1)];

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
        bool animated = _animateNextContent && stack.View?.Window is not null;
        _animateNextContent = false;
        if (controller is null) stack.SetViewControllers([root], false);
        else if (root is MailRootViewController)
        {
            // The message list is the Mail stack's root; the drawer closes over it.
            PrepareForTransition(controller, stack);
            stack.SetViewControllers([controller], false);
        }
        else if (root is ModeMenuRootViewController)
        {
            PrepareForTransition(controller, stack);
            stack.SetViewControllers([root, controller], animated);
        }
        else
        {
            controller.NavigationItem.RightBarButtonItem ??= SettingsButton();
            PrepareForTransition(controller, stack);
            stack.SetViewControllers([controller], false);
        }
    }

    protected override void ModeChanged(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings) return;
        if (mode != WinoApplicationMode.Mail) _drawer.Close(false);
        var index = IndexOf(mode);
        if (_tabs.SelectedIndex != index) _tabs.SelectedIndex = index;
    }

    protected override void MenuChanged(ShellMenu? menu)
    {
        _mailboxes.SetMenu(ActiveMode == WinoApplicationMode.Mail ? menu : null);
        foreach (var root in _modeRoots) root.SetMenu(ActiveMode == root.Mode ? menu : null);
    }

    protected override void SelectedItemChanged(object? item)
    {
        _mailboxes.SetSelected(ActiveMode == WinoApplicationMode.Mail ? item : null);
        foreach (var root in _modeRoots) root.SetSelected(ActiveMode == root.Mode ? item : null);
    }

    // ---- Mode menu roots (To Do Lists) ----

    /// <summary>
    /// A row on a mode's menu root: the shell selects it, and when it selects a surface the content page slides in again
    /// (a page popped back to the root stays the shell's content, as the To Do page does).
    /// </summary>
    internal void OpenFromModeRoot(IMenuItem item)
    {
        _animateNextContent = true;
        SelectMenuItem(item);
        if (Wino.Mail.ViewModels.Data.ShellMenuItemRules.SelectsOnInvoked(item)) ShowRetainedContent();
    }

    /// <summary>Pushes the shell's content again over its mode root when it was popped back to the root.</summary>
    private void ShowRetainedContent()
    {
        if (Content is not { } content || ActiveMode is not { } mode) return;
        var stack = _stacks[IndexOf(mode)];
        if (_roots[IndexOf(mode)] is not ModeMenuRootViewController || Array.IndexOf(stack.ViewControllers ?? [], content) >= 0) return;
        _animateNextContent = false;
        PrepareForTransition(content, stack);
        stack.PushViewController(content, true);
    }

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

    /// <summary>Opens the Mailboxes drawer (the list's sidebar button, the left-edge pan).</summary>
    internal void OpenMailboxes()
    {
        if (CanOpenMailboxes()) _drawer.Open(true);
    }

    /// <summary>Closes the Mailboxes drawer.</summary>
    internal void CloseMailboxes(bool animated = true) => _drawer.Close(animated);

    /// <summary>
    /// The drawer opens over the Mail stack's root page only: not over a pushed reader, a sheet, or the list in edit mode.
    /// </summary>
    private bool CanOpenMailboxes()
    {
        if (ActiveMode != WinoApplicationMode.Mail || _tabs.SelectedIndex != IndexOf(WinoApplicationMode.Mail) || PresentedViewController is not null) return false;
        var stack = MailStack;
        if ((stack.ViewControllers?.Length ?? 0) != 1) return false;
        return stack.TopViewController is not MailListPageViewController { IsInSelectionMode: true };
    }

    /// <summary>The drawer's Settings row: Settings opens as the avatar used to open it.</summary>
    internal void OpenSettingsFromMailboxes() => _drawer.Close(true, OpenSettings);

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
            => navigationController.SetToolbarHidden(viewController.ToolbarItems is not { Length: > 0 }, animated);

        public override void DidShowViewController(UINavigationController navigationController, UIViewController viewController, bool animated)
            => shell.PruneReaderToolbars();
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
                if (!CanOpenMailboxes()) return "cannot open";
                _drawer.Open(true);
                return "ok";
            case "close":
                _drawer.Close(true);
                return "ok";
            default:
                return $"open={_drawer.IsOpen} canOpen={CanOpenMailboxes()}";
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
/// the sidebar button that opens Mailboxes and the New Message button, like the message list's bar.
/// </summary>
internal sealed class MailRootViewController : UIViewController
{
    private readonly PhoneShellViewController _shell;

    public MailRootViewController(PhoneShellViewController shell)
    {
        _shell = shell;
        NavigationItem.Title = Translator.IOS_MailboxesTitle;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        NavigationItem.LeftBarButtonItem = PhoneBarItems.Button(WinoIconGlyph.PanelLeft, Translator.IOS_MailboxesTitle, shell.OpenMailboxes);
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
