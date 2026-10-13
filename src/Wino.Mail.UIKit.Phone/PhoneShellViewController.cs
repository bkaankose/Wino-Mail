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
/// The iPhone shell (iOS design decisions): a tab bar with Mail, Calendar, Contacts and To Do, each a navigation stack,
/// over the theme backdrop. Tabs switch modes through the shared shell ViewModel; Settings is never a tab, the account
/// avatar opens it as a full-screen sheet. A mode's root page becomes its stack's content. Modes with a menu root keep a
/// shell-owned list under their content, so the content has a back control to it: Mail over Mailboxes (like Mail on
/// iPhone), To Do over Lists (<see cref="ModeMenuRootViewController"/>); Calendar and Contacts content is the stack's
/// root. The shell also owns the Mail stack's toolbar: the reader toolbar while the reader is
/// on top, the list's edit toolbar in edit mode, hidden otherwise.
/// </summary>
public sealed class PhoneShellViewController : UIKitShellViewController
{
    private readonly UITabBarController _tabs = new();
    private readonly UINavigationController[] _stacks;
    private readonly UIViewController[] _roots;
    private readonly MailboxesViewController _mailboxes;
    private readonly ModeMenuRootViewController[] _modeRoots;
    private readonly IReadOnlyList<WinoModeSegment> _segments = WinoModeSegments.AppModes;
    private readonly StackDelegate _stackDelegate;
    private readonly List<(WeakReference<UIViewController> Reader, MailReaderToolbar Toolbar)> _readerToolbars = [];
    private bool _animateNextContent;

    public PhoneShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, navigation, state, services, dispatcher, logger)
    {
        _mailboxes = new MailboxesViewController(this, services);
        _modeRoots = [new ModeMenuRootViewController(this, WinoApplicationMode.Tasks, dispatcher, new ToDoMenuRowStyle())];
        _stackDelegate = new StackDelegate(this);
        _roots = _segments.Select(segment => segment.Mode == WinoApplicationMode.Mail
            ? _mailboxes
            : _modeRoots.FirstOrDefault(root => root.Mode == segment.Mode) ?? (UIViewController)new ModePlaceholderViewController(segment)).ToArray();
        _stacks = _roots.Select((root, index) => CreateStack(root, _segments[index])).ToArray();
    }

    private UINavigationController CreateStack(UIViewController root, WinoModeSegment segment)
    {
        // Mailboxes puts the avatar on its leading side next to its own compose button.
        if (root is not MailboxesViewController) root.NavigationItem.RightBarButtonItem = AvatarButton();
        var stack = new UINavigationController(root) { Delegate = _stackDelegate };
        stack.NavigationBar.PrefersLargeTitles = true;
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

    private UIBarButtonItem AvatarButton()
    {
        var item = new UIBarButtonItem(WinoIcons.TemplateImage(WinoIconGlyph.Person, 22),
            UIBarButtonItemStyle.Plain, (_, _) => OpenSettings());
        item.AccessibilityLabel = Translator.MenuSettings;
        return item;
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
        View = root;
        BindShell();
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
        else if (root is MailboxesViewController or ModeMenuRootViewController) stack.SetViewControllers([root, controller], animated);
        else
        {
            controller.NavigationItem.RightBarButtonItem ??= AvatarButton();
            stack.SetViewControllers([controller], false);
        }
    }

    protected override void ModeChanged(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings) return;
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
        stack.PushViewController(content, true);
    }

    // ---- Mail flow hooks (Mailboxes, the message list and the reader) ----

    /// <summary>The Mail provider while Mail is the active mode (synchronization status for Mailboxes).</summary>
    internal IShellMenuProvider? MailProvider => ActiveMode == WinoApplicationMode.Mail ? ViewModel.CurrentProvider : null;

    /// <summary>The shell's selected menu item.</summary>
    internal object? SelectedMenuItem => ViewModel.SelectedMenuItem;

    /// <summary>A Mailboxes row tap: the folder list that follows slides in.</summary>
    internal void OpenFromMailboxes(IMenuItem item)
    {
        _animateNextContent = true;
        SelectMenuItem(item);
    }

    /// <summary>
    /// Pushes the folder list again when it was popped back to Mailboxes but is still the shell's live content
    /// (MailListPageViewController stays active while popped). False when there is nothing to show again.
    /// </summary>
    internal bool ShowRetainedMailContent()
    {
        if (ActiveMode != WinoApplicationMode.Mail || Content is not MailListPageViewController { IsRetained: true } list) return false;
        var stack = MailStack;
        if (Array.IndexOf(stack.ViewControllers ?? [], list) >= 0) return false;
        stack.PushViewController(list, true);
        return true;
    }

    /// <summary>The New Message button: the shared Mail provider creates the draft, which opens in the composer sheet.</summary>
    internal void Compose()
    {
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
