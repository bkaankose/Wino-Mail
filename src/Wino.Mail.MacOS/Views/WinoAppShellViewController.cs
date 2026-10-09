using System.ComponentModel;
using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Shell.ViewModels;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// The main window's content: the theme backdrop under everything, a split with the Wino shell
/// pane and the mode content, and the unified toolbar that replaces the Windows title bar
/// (docs/macos-design-decisions.md, Revision 2). Modes activate like the Windows WinoAppShell:
/// the provider publishes its menu and navigates its root page into the content host. The shell
/// never inspects its content page beyond <see cref="IShellCommandTarget"/> and <see cref="IShellSearchTarget"/>.
/// </summary>
public sealed class WinoAppShellViewController : WinoViewController<WinoAppShellViewModel>
{
    private const double BriefingPanelWidth = 400;

    private readonly INavigationService _navigation;
    private readonly IStatePersistanceService _state;
    private readonly IServiceProvider _services;
    private readonly NSSplitViewController _split = new();
    private readonly NSViewController _contentHost = new();
    private readonly NSView _content = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoZoneView _emptyZone = new();
    private ShellSidebarViewController? _sidebar;
    private NSSplitViewItem? _sidebarItem;
    private ShellToolbar? _toolbar;
    private NSViewController? _child;
    private IShellCommandTarget? _commandTarget;
    private IShellMenuProvider? _provider;
    private INotifyPropertyChanged? _titleSource;
    private WinoApplicationMode? _activeMode;
    private IDailyBriefingPresenter? _briefing;
    private NSViewController? _briefingPanel;
    private bool _briefingOpen;

    public WinoAppShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _state = state;
        _services = services;
    }

    public IShellCommandTarget? CommandTarget => _commandTarget;
    public IShellSearchTarget? SearchTarget => _child as IShellSearchTarget;

    /// <summary>The mode currently on screen.</summary>
    public WinoApplicationMode? ActiveMode => _activeMode;

    /// <summary>The mail shell client, available in every mode (sidebar context menus use it).</summary>
    internal IMailShellClient? MailClient => _services.GetService<IMailShellClient>();

    /// <summary>The application router.</summary>
    internal AppKitNavigationService Navigation => _services.GetRequiredService<AppKitNavigationService>();

    public override void LoadView()
    {
        var pictures = _services.GetService<IPictureStorageService>();
        var context = new ShellPaneContext(
            account => pictures is null
                ? MailAccountIconInfoFactory.CreateProviderFallback(account.ProviderType, account.SpecialImapProvider)
                : MailAccountIconInfoFactory.Create(account, pictures),
            () => ViewModel.PreferencesService.FirstDayOfWeek);
        var menus = new ShellSidebarMenuContext(MailClient, Navigation, Dispatcher, ReportError, FixAccountAsync);
        _sidebar = new ShellSidebarViewController(Dispatcher, ReportError, Select, SwitchMode, FixAccount, context, menus);
        _sidebarItem = NSSplitViewItem.FromViewController(_sidebar);
        _sidebarItem.MinimumThickness = 220;
        _sidebarItem.MaximumThickness = 380;
        _sidebarItem.CanCollapse = true;
        _sidebarItem.CanCollapseFromWindowResize = true;
        _sidebarItem.HoldingPriority = 262;
        // The Windows pane opens at 280; a soft width keeps it there when the content page changes.
        var preferredWidth = _sidebar.View.WidthAnchor.ConstraintEqualTo(280);
        preferredWidth.Priority = 251;
        preferredWidth.Active = true;

        // Pages start below the toolbar; while no page is shown an empty zone keeps the layout.
        _contentHost.View = _content;
        PinBelowToolbar(_emptyZone);
        var contentItem = NSSplitViewItem.FromViewController(_contentHost);
        contentItem.MinimumThickness = 560;

        // Windows draws no line between the pane and the content: a thin, transparent divider.
        _split.SplitView = new ClearDividerSplitView();
        _split.SplitView.IsVertical = true;
        _split.SplitView.DividerStyle = NSSplitViewDividerStyle.Thin;
        _split.SplitView.AutosaveName = "WinoShellSplit.v3";
        _split.AddSplitViewItem(_sidebarItem);
        _split.AddSplitViewItem(contentItem);

        // The theme backdrop fills the whole window: title bar, pane and content sit on it.
        var root = new NSView();
        WinoLayout.Fill(new ThemeBackdropView(), root);
        AddChildViewController(_split);
        Layout.Fill(_split.View, root);
        View = root;
        bool hasSavedLayout = Foundation.NSUserDefaults.StandardUserDefaults["NSSplitView Subview Frames WinoShellSplit.v3"] is not null;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_sidebarItem is { Collapsed: true }) _sidebarItem.Collapsed = false;
            if (!hasSavedLayout) _split.SplitView.SetPositionOfDivider(280, 0);
        });

        _toolbar = new ShellToolbar(_split.SplitView, () => SearchTarget, ToggleSidebar, Synchronize, ToggleBriefing, ShowWhatsNew, OpenAccount, ReportError);
        _toolbar.SetAccountHidden(ViewModel.PreferencesService.IsWinoAccountButtonHidden);

        Bindings.Own(new PropertyBinding<WinoAppShellViewModel, ShellMenu?>(ViewModel, nameof(ViewModel.CurrentMenu),
            vm => vm.CurrentMenu, menu => _sidebar.Bind(menu), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WinoAppShellViewModel, object?>(ViewModel, nameof(ViewModel.SelectedMenuItem),
            vm => vm.SelectedMenuItem, item => { _sidebar.SetSelectedItem(item as IMenuItem); ObserveTitle(item as INotifyPropertyChanged); UpdateTitle(); }, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WinoAppShellViewModel, IShellMenuProvider?>(ViewModel, nameof(ViewModel.CurrentProvider),
            vm => vm.CurrentProvider, ObserveProvider, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WinoAppShellViewModel, WinoApplicationMode>(ViewModel, nameof(ViewModel.CurrentMode),
            vm => vm.CurrentMode, mode => { _sidebar.SetMode(mode); UpdateTitle(); }, Dispatcher, ReportError));
#if DEBUG
        // "folder NAME" invokes the first folder menu item with that name, as a sidebar click would.
        // "account TEXT" invokes the account menu item whose account name or address contains TEXT.
        Infrastructure.MacDebugBridge.Register("newmail", _ =>
        {
            var item = ViewModel.CurrentMenu?.Items.OfType<NewMailMenuItem>().FirstOrDefault();
            if (item is null) return Task.FromResult("not found");
            Select(item);
            return Task.FromResult("ok");
        });
        Infrastructure.MacDebugBridge.Register("account-select", args =>
        {
            var text = string.Join(' ', args);
            var match = ViewModel.CurrentMenu?.Items.OfType<IAccountMenuItem>().FirstOrDefault(item => item.HoldingAccounts.Any(account =>
                (account.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || (account.Address?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)));
            if (match is null) return Task.FromResult("not found");
            Select(match);
            return Task.FromResult("ok");
        });
        Infrastructure.MacDebugBridge.Register("folder", args =>
        {
            // "folder ACCOUNT:NAME" limits the match to the account whose name or address contains ACCOUNT.
            var name = string.Join(' ', args);
            string? accountFilter = null;
            if (name.IndexOf(':') is var colon and > 0) { accountFilter = name[..colon]; name = name[(colon + 1)..]; }
            static IEnumerable<IMenuItem> Walk(IEnumerable<IMenuItem> items) => items.SelectMany(item =>
                new[] { item }.Concat(item.GetType().GetProperties().FirstOrDefault(p => p.Name == "SubMenuItems")?.GetValue(item) is System.Collections.IEnumerable children
                    ? Walk(children.OfType<IMenuItem>()) : []));
            var match = ViewModel.CurrentMenu is { } menu
                ? Walk(menu.Items).OfType<IBaseFolderMenuItem>().FirstOrDefault(f => string.Equals(f.FolderName, name, StringComparison.OrdinalIgnoreCase)
                    && (accountFilter is null || f is IFolderMenuItem { ParentAccount: { } account }
                        && ((account.Name?.Contains(accountFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (account.Address?.Contains(accountFilter, StringComparison.OrdinalIgnoreCase) ?? false))))
                : null;
            if (match is null) return Task.FromResult("not found");
            Select(match);
            return Task.FromResult("ok");
        });
#endif
        _state.PropertyChanged += StateChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            _state.PropertyChanged -= StateChanged;
            ObserveProvider(null);
            ObserveTitle(null);
            ObserveBriefing(null);
        }));
        ObserveBriefing(_services.GetService<IDailyBriefingPresenter>());
        _ = RefreshWhatsNewAsync();
    }

    /// <summary>Called by the shell window once it exists, to install the toolbar.</summary>
    public void AttachToWindow(NSWindow window)
    {
        _ = View;
        window.Toolbar = _toolbar!.CreateToolbar();
        window.ToolbarStyle = NSWindowToolbarStyle.Unified;
        UpdateTitle();
        _toolbar.Revalidate();
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        var start = _state.ApplicationMode;
        if (start == WinoApplicationMode.Settings || _navigation is AppKitNavigationService router && !router.IsModeAvailable(start))
            start = WinoApplicationMode.Mail;
        await ActivateModeAsync(start, new ShellModeActivationContext { IsInitialActivation = true });
    }

    /// <summary>
    /// Switches the shell to a mode the way the Windows WinoAppShell.ActivateMode does: release the
    /// outgoing provider's menu, publish the new provider and let it navigate its root page into the
    /// content host. A mode whose page is not built yet leaves the content empty rather than failing.
    /// </summary>
    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        context ??= new ShellModeActivationContext { IsInitialActivation = false };
        IShellMenuProvider provider;
        try { provider = ViewModel.GetProvider(mode); }
        catch (Exception exception) { ReportError(exception); return; }

        if (_activeMode == mode && _child is not null)
        {
            if (context.Parameter is not null) provider.ActivateShellMenu(context);
            return;
        }

        _state.ApplicationMode = mode;
        _state.AppModeTitle = ModeTitle(mode);
        _state.CoreWindowTitle = string.Empty;
        ViewModel.SetShellMenu(null);
        if (_activeMode is { } previous && ViewModel.TryGetProvider(previous, out var outgoing) && outgoing is not null)
        {
            try { outgoing.ReleaseShellMenu(); }
            catch (Exception exception) { ReportError(exception); }
        }
        _activeMode = mode;
        ViewModel.SetCurrentMode(mode);
        await ClearContentAsync();
        if (!ReferenceEquals(provider.Dispatcher, Dispatcher)) provider.Dispatcher = Dispatcher;
        try
        {
            // Mail awaits its first folder route; the other modes navigate through the router themselves.
            if (provider is MailAppShellViewModel mail) await mail.InitializeNavigationAsync(NavigationMode.New, context);
            else provider.ActivateShellMenu(context);
        }
        catch (Exception exception) { ReportError(exception); }
        if (_activeMode != mode) return;
        ViewModel.SetShellMenu(provider);
        await Dispatcher.ExecuteOnUIThread(UpdateTitle);
    }

    private static string ModeTitle(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => "Wino " + Translator.KeyboardShortcuts_ModeCalendar,
        WinoApplicationMode.Contacts => "Wino " + Translator.ContactsPage_Title,
        WinoApplicationMode.Tasks => "Wino " + Translator.ToDoPage_Title,
        _ => "Wino Mail"
    };

    public async Task SetContentAsync(NSViewController controller, object? parameter)
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            DetachCommandTarget();
            _child?.View.RemoveFromSuperview();
            _child?.RemoveFromParentViewController();
            _child?.Dispose();
            _child = controller;
            _emptyZone.Hidden = true;
            _contentHost.AddChildViewController(controller);
            PinBelowToolbar(controller.View);
            if (_briefingPanel is { } panel) _content.AddSubview(panel.View, NSWindowOrderingMode.Above, controller.View);
            AttachCommandTarget(controller as IShellCommandTarget);
            _toolbar?.ResetSearch();
        });
        if (controller is IWinoViewController next) await next.ActivateAsync(NavigationMode.New, parameter);
    }

    public async Task ClearContentAsync()
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            DetachCommandTarget();
            _child?.View.RemoveFromSuperview();
            _child?.RemoveFromParentViewController();
            _child?.Dispose();
            _child = null;
            _emptyZone.Hidden = false;
        });
    }

    public override async Task ReleaseAsync()
    {
        if (_child is IWinoViewController child) await child.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            DetachCommandTarget();
            _child?.Dispose();
            _child = null;
            ViewModel.ShutdownProviders();
        });
        await base.ReleaseAsync();
    }

    /// <summary>
    /// The window uses a full-size content view so the pane runs under the toolbar; content
    /// pages start below it, at the content area's safe-area top, while the backdrop fills behind.
    /// The empty zone keeps the Windows inner-frame gutters (0, 0, 7, 7).
    /// </summary>
    private void PinBelowToolbar(NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _content.AddSubview(view);
        bool isZone = ReferenceEquals(view, _emptyZone);
        NSLayoutConstraint.ActivateConstraints(
        [
            view.TopAnchor.ConstraintEqualTo(_content.SafeAreaLayoutGuide.TopAnchor, isZone ? 2 : 0),
            view.LeadingAnchor.ConstraintEqualTo(_content.LeadingAnchor),
            view.TrailingAnchor.ConstraintEqualTo(_content.TrailingAnchor, isZone ? -7 : 0),
            view.BottomAnchor.ConstraintEqualTo(_content.BottomAnchor, isZone ? -7 : 0)
        ]);
    }

    /// <summary>Menu-bar entry point for mail actions.</summary>
    public void ExecuteCommand(ShellCommand command)
    {
        if (_commandTarget is { } target && target.CanExecute(command)) target.Execute(command, null);
    }

    public bool CanExecuteCommand(ShellCommand command) => _commandTarget?.CanExecute(command) == true;

    /// <summary>The content page's ViewModel, for keyboard shortcut eligibility (Windows rootPage.AssociatedViewModel).</summary>
    public object? ContentViewModel => (_child as IViewModelHost)?.AssociatedViewModel;

    /// <summary>
    /// Windows KeyboardShortcutController.DispatchAsync for the root page: the content page's ViewModel
    /// handles the shortcut first, then the mode's provider (WinoAppShellViewModel.KeyboardShortcutHookForMode).
    /// </summary>
    public async Task RouteKeyboardShortcutAsync(Wino.Core.Domain.Models.KeyboardShortcutTriggerDetails details)
    {
        if (ContentViewModel is Wino.Core.ViewModels.CoreBaseViewModel page) await page.KeyboardShortcutHook(details);
        if (!details.Handled) await ViewModel.KeyboardShortcutHookForMode(details);
    }

    public void ToggleSidebar()
    {
        if (_sidebarItem is null) return;
        ((NSSplitViewItem)_sidebarItem.Animator).Collapsed = !_sidebarItem.Collapsed;
    }

    public void FocusSearch() => _toolbar?.FocusSearch();

    /// <summary>Raised when the current page's command availability changes, for menu validation.</summary>
    public event EventHandler? CommandStateChanged;

    /// <summary>File › New: the current mode's "new item" pane entry (new mail, event, contact or list).</summary>
    public void NewItem()
    {
        if (_provider is IMailShellClient mail) { Select(mail.CreatePrimaryMenuItem); return; }
        var item = ViewModel.CurrentMenu?.Items.FirstOrDefault(entry => entry is NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem);
        if (item is not null) Select(item);
    }

    public void OpenSettings() => _navigation.ChangeApplicationMode(WinoApplicationMode.Settings);

    private void SwitchMode(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings)
        {
            OpenSettings();
            if (_activeMode is { } current) _sidebar?.SetMode(current);
            return;
        }
        _navigation.ChangeApplicationMode(mode);
    }

    private void OpenAccount(NSView anchor)
    {
        if (_services.GetService<IWinoAccountPresenter>() is { } presenter) { presenter.Show(anchor); return; }
        if (!_navigation.Navigate(WinoPage.WinoAccountManagementPage)) OpenSettings();
    }

    private async void FixAccount(IMenuItem item)
    {
        try { await FixAccountAsync(item); }
        catch (Exception exception) { ReportError(exception); }
    }

    private Task FixAccountAsync(IMenuItem item)
        => _provider is IMailShellClient mail && item is IAccountNavigationMenuItem { Account: { } account }
            ? mail.HandleAccountAttentionAsync(account)
            : ViewModel.InvokeMenuItemAsync(item);

    private async void Synchronize()
    {
        try { if (_provider is { CanSynchronize: true } provider) await provider.SynchronizeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Select(IMenuItem item)
    {
        try
        {
            if (!ShellPaneRows.IsEnabled(item)) return;
            // Folders select themselves when their navigation starts (MailAppShellViewModel.NavigateFolderAsync).
            // Pre-selecting them here would make that navigation look like a repeat of the current
            // folder and skip it, leaving the previous folder's list on screen. Windows also raises
            // ItemInvoked before the navigation view changes its selection.
            if (ViewModel.HandlesSelection && ShellPaneRows.SelectsOnInvoked(item) && item is not IBaseFolderMenuItem)
                ViewModel.SelectedMenuItem = item;
            await ViewModel.InvokeMenuItemAsync(item);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    #region Daily briefing, What's New

    private void ObserveBriefing(IDailyBriefingPresenter? presenter)
    {
        if (_briefing is not null) _briefing.UnseenItemsChanged -= BriefingChanged;
        _briefing = presenter;
        if (presenter is not null) presenter.UnseenItemsChanged += BriefingChanged;
        ApplyBriefing();
    }

    private async void BriefingChanged(object? sender, EventArgs args)
    {
        try { await Dispatcher.ExecuteOnUIThread(ApplyBriefing); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ApplyBriefing() => _toolbar?.SetBriefing(_briefing is not null, _briefing?.HasUnseenItems == true, _briefingOpen);

    /// <summary>Shows or hides the briefing panel as a right overlay, 400pt wide, under the title bar.</summary>
    private void ToggleBriefing(NSView anchor)
    {
        if (_briefing is null) return;
        if (_briefingPanel is null)
        {
            _briefingPanel = _briefing.CreatePanel(() => SetBriefingOpen(false));
            _contentHost.AddChildViewController(_briefingPanel);
            var panel = _briefingPanel.View;
            panel.TranslatesAutoresizingMaskIntoConstraints = false;
            panel.Hidden = true;
            _content.AddSubview(panel);
            NSLayoutConstraint.ActivateConstraints(
            [
                panel.TopAnchor.ConstraintEqualTo(_content.SafeAreaLayoutGuide.TopAnchor),
                panel.TrailingAnchor.ConstraintEqualTo(_content.TrailingAnchor),
                panel.BottomAnchor.ConstraintEqualTo(_content.BottomAnchor),
                panel.WidthAnchor.ConstraintEqualTo((nfloat)BriefingPanelWidth)
            ]);
        }
        SetBriefingOpen(!_briefingOpen);
    }

    private void SetBriefingOpen(bool open)
    {
        _briefingOpen = open;
        if (_briefingPanel is { } panel) panel.View.Hidden = !open;
        ApplyBriefing();
    }

    private async Task RefreshWhatsNewAsync()
    {
        try
        {
            var service = _services.GetService<IWhatsNewService>();
            var presenter = _services.GetService<IWhatsNewPresenter>();
            bool show = service is not null && presenter is not null && await service.ShouldShowShellEntryAsync();
            await Dispatcher.ExecuteOnUIThread(() => _toolbar?.SetWhatsNewVisible(show));
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void ShowWhatsNew()
    {
        try
        {
            if (_services.GetService<IWhatsNewPresenter>() is not { } presenter) return;
            await presenter.ShowAsync();
            _services.GetService<IWhatsNewService>()?.MarkOpenedForCurrentVersion();
            _toolbar?.SetWhatsNewVisible(false);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    #endregion

    private void AttachCommandTarget(IShellCommandTarget? target)
    {
        _commandTarget = target;
        if (target is not null) target.CommandStateChanged += TargetStateChanged;
        TargetStateChanged(this, EventArgs.Empty);
    }

    private void DetachCommandTarget()
    {
        if (_commandTarget is not null) _commandTarget.CommandStateChanged -= TargetStateChanged;
        _commandTarget = null;
        TargetStateChanged(this, EventArgs.Empty);
    }

    private async void TargetStateChanged(object? sender, EventArgs args)
    {
        try
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _toolbar?.Revalidate();
                CommandStateChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ObserveProvider(IShellMenuProvider? provider)
    {
        if (_provider is not null) _provider.PropertyChanged -= ProviderChanged;
        _provider = provider;
        if (provider is not null) provider.PropertyChanged += ProviderChanged;
        ApplySynchronization();
    }

    private async void ProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IShellMenuProvider.IsSynchronizationSupported) or nameof(IShellMenuProvider.CanSynchronize)
            or nameof(IShellMenuProvider.SynchronizationState) or nameof(IShellMenuProvider.SynchronizationDescription) or nameof(IShellMenuProvider.SynchronizationToolTip))) return;
        try { await Dispatcher.ExecuteOnUIThread(ApplySynchronization); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ApplySynchronization()
    {
        var provider = _provider;
        if (provider is null) { _toolbar?.SetSynchronization(false, false, false, null); return; }
        var state = provider.SynchronizationState;
        var tip = state.IsSynchronizing && !string.IsNullOrWhiteSpace(provider.SynchronizationDescription)
            ? provider.SynchronizationDescription
            : provider.SynchronizationToolTip;
        _toolbar?.SetSynchronization(provider.IsSynchronizationSupported, provider.CanSynchronize || state.IsSynchronizing, state.IsSynchronizing, tip);
    }

    private void ObserveTitle(INotifyPropertyChanged? source)
    {
        if (_titleSource is not null) _titleSource.PropertyChanged -= TitleSourceChanged;
        _titleSource = source;
        if (source is not null) source.PropertyChanged += TitleSourceChanged;
    }

    private async void TitleSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IBaseFolderMenuItem.UnreadItemCount) or nameof(IBaseFolderMenuItem.FolderName))) return;
        try { await Dispatcher.ExecuteOnUIThread(UpdateTitle); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IStatePersistanceService.CoreWindowTitle) or nameof(IStatePersistanceService.AppModeTitle))) return;
        try { await Dispatcher.ExecuteOnUIThread(UpdateTitle); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>
    /// Window title and subtitle in the unified toolbar: in Mail the selected folder, then its account
    /// and unread count; in the other modes the mode title and whatever the page reports.
    /// </summary>
    private void UpdateTitle()
    {
        if (View.Window is not { } window) return;
        string title;
        string subtitle = string.Empty;
        if (_activeMode is WinoApplicationMode.Mail or null && ViewModel.SelectedMenuItem is IBaseFolderMenuItem folder)
        {
            title = folder.FolderName ?? string.Empty;
            var owner = folder is IFolderMenuItem { ParentAccount: { } account }
                ? (string.IsNullOrWhiteSpace(account.Address) ? account.Name : account.Address)
                : folder.AssignedAccountName;
            var unread = folder.ShowUnreadCount && folder.UnreadItemCount > 0 ? $"{folder.UnreadItemCount} unread" : null;
            subtitle = string.Join(" · ", new[] { owner, unread }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        else
        {
            title = !string.IsNullOrWhiteSpace(_state.AppModeTitle) ? _state.AppModeTitle : "Wino Mail";
            subtitle = _state.CoreWindowTitle ?? string.Empty;
        }
        window.Title = string.IsNullOrWhiteSpace(title) ? "Wino Mail" : title;
        window.Subtitle = subtitle;
    }

    /// <summary>A split view whose 1 pt divider stays draggable but draws nothing.</summary>
    private sealed class ClearDividerSplitView : NSSplitView
    {
        public override NSColor DividerColor => NSColor.Clear;

        public override void DrawDivider(CoreGraphics.CGRect rect)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachCommandTarget();
            ObserveBriefing(null);
            _sidebar?.Dispose();
        }
        base.Dispose(disposing);
    }
}
