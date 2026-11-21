using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Controls.Linux.Shell;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell;
using Wino.Mail.ViewModels.Shell.Contracts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The shell: <see cref="ShellSidebarView"/> beside the content area that <see cref="ShellLayout"/> arranges, with the
/// reading pane beside pages that use it.
/// </summary>
internal sealed class ShellView : WinoView<WinoAppShellViewModel>
{
    private readonly GtkNavigationService _navigation;
    private readonly ShellModeActivator _modes;
    private readonly ReadingPaneHost _pane;
    private readonly ShellReadingPane _reading;
    private readonly ShellLayout _layout;
    private readonly ShellSidebarView _sidebar;
    private readonly ShellHeaderBar _header;
    private readonly WinoModeSwitcher _switcher = new();
    private readonly Dictionary<WinoApplicationMode, Gtk.Widget> _placeholders = [];
    private readonly IServiceProvider _services;
    private readonly IDailyBriefingPresenter? _briefing;
    private readonly IWhatsNewPresenter? _whatsNew;
    private readonly IWinoAccountPresenter? _account;
    private readonly PropertyChangedEventHandler _providerChanged;
    private readonly PropertyChangedEventHandler _titleSourceChanged;
    private readonly PropertyChangedEventHandler _stateChanged;
    private readonly EventHandler<string> _preferenceChanged;
    private readonly EventHandler _briefingChanged;
    private IShellMenuProvider? _provider;
    private INotifyPropertyChanged? _titleSource;
    private IWinoView? _content;
    private int _contentVersion;

    public ShellView(WinoAppShellViewModel viewModel, WinoViewServices services, GtkNavigationService navigation, WindowStateStore store, IServiceProvider serviceProvider) : base(viewModel, services)
    {
        _navigation = navigation;
        _services = serviceProvider;
        _briefing = serviceProvider.GetService<IDailyBriefingPresenter>();
        _whatsNew = serviceProvider.GetService<IWhatsNewPresenter>();
        _account = serviceProvider.GetService<IWinoAccountPresenter>();
        _providerChanged = OnProviderChanged;
        _titleSourceChanged = OnTitleSourceChanged;
        _stateChanged = OnStateChanged;
        _preferenceChanged = OnPreferenceChanged;
        _briefingChanged = (_, _) => RunOnUI(ApplyBriefing);
        _modes = new ShellModeActivator(ViewModel, ViewModel.StatePersistenceService, exception => Serilog.Log.Error(exception, "Shell mode activation failed."));
        _pane = new ReadingPaneHost(ReportError);
        _layout = new ShellLayout(ViewModel.PreferencesService, ViewModel.StatePersistenceService, store);
        _reading = new ShellReadingPane(_pane, _layout);
        _sidebar = new ShellSidebarView(ViewModel, Bind);
        _sidebar.ItemInvoked += _layout.DismissOverlaySidebar;
        _layout.SetSidebar(_sidebar.Root);
        _sidebar.SetFooter(_switcher.Root);
        _switcher.ModeSelected += SwitchMode;
        _layout.ReadingPane = _pane.Root;
        _header = new ShellHeaderBar(Translator.Buttons_Sync, ReportError);
        _layout.SetHeader(_header.Root);
        _layout.StateChanged += OnLayoutStateChanged;
        WireHeader();
        OnLayoutStateChanged();
    }

    public override Gtk.Widget Root => _layout.Root;
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;
    public object? ContentViewModel { get; private set; }
    internal ShellLayout Layout => _layout;
    internal ShellSidebarView Sidebar => _sidebar;
    internal IReadOnlyList<WinoApplicationMode>? VisibleModes => ViewModel.ModeVisibility?.VisibleModes;

    public async Task SetContentAsync(IWinoView view, object? parameter)
    {
        if (IsReleased)
        {
            await view.ReleaseAsync();
            return;
        }

        var version = ++_contentVersion;
        var previous = _content;
        _layout.SetContent(view.Root, view is IUsesReadingPane);
        _content = view;
        ContentViewModel = view.AssociatedViewModel;
        _header.SetSearchTarget(view as IShellSearchTarget);
        _header.ResetSearch();
        if (previous is not null) await ReleaseContentAsync(previous);
        if (version != _contentVersion) return;
        await view.ActivateAsync(NavigationMode.New, parameter);
        if (!IsReleased) RefreshBack();
        // The coordinator records the history only after this returns, so back is evaluated again once its navigation ends.
        Observe(RefreshBackAfterAsync(_navigation.PendingNavigation));
    }

    public async Task ClearContentAsync()
    {
        ++_contentVersion;
        var previous = _content;
        _content = null;
        ContentViewModel = null;
        if (!IsReleased)
        {
            _layout.SetContent(null, false);
            _layout.ShowDetail(false);
            _header.SetSearchTarget(null);
        }
        if (previous is not null) await ReleaseContentAsync(previous);
        if (!IsReleased) RefreshBack();
    }

    internal void HandleAction(string name)
    {
        if (IsReleased) return;
        switch (name)
        {
            case AppActions.ToggleSidebar: _layout.ToggleSidebar(); break;
            case AppActions.Synchronize: Synchronize(); break;
            case AppActions.Back: GoBack(); break;
            case AppActions.Search: _header.FocusSearch(); break;
            case AppActions.WhatsNew: ShowWhatsNew(); break;
            case AppActions.Briefing: break;
            case AppActions.Account: OpenAccount(_header.Root); break;
        }
    }

#if DEBUG
    internal string DescribeHeader() => _header.DescribeState();

    internal string DescribeModes()
    {
        var text = new System.Text.StringBuilder();
        text.Append("ActiveMode=").AppendLine(_modes.ActiveMode?.ToString() ?? "-");
        text.Append("CurrentMode=").AppendLine(ViewModel.CurrentMode.ToString());
        text.Append("Switcher=").AppendLine(_switcher.Selected.ToString());
        text.Append("Visible=").AppendLine(VisibleModes is { } visible ? string.Join(",", visible) : "-");
        text.Append("Available=").AppendLine(string.Join(",", Wino.Core.Domain.Models.Accounts.AppModeVisibility.SwitchableModes.Select(mode => $"{mode}:{_navigation.IsModeAvailable(mode)}")));
        text.Append("Placeholder=").AppendLine((_modes.ActiveMode is { } active && !_navigation.IsModeAvailable(active)).ToString());
        var rows = Wino.Mail.ViewModels.Data.ShellMenuRowPresentation.Flatten(ViewModel.CurrentMenu?.Items);
        text.Append("MenuRows=").AppendLine(rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (item, depth) in rows.Take(10))
            text.Append("Row=").AppendLine(Wino.Mail.ViewModels.Data.ShellMenuRowPresentation.Describe(item, depth, null).Title);
        return text.ToString();
    }
#endif

    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        if (IsReleased) return;
        await _modes.ActivateAsync(mode, context, _navigation.ClearShellContentAsync, Dispatcher, () => _content is not null, ModeTitle);
        if (!IsReleased) UpdatePlaceholder();
    }

    internal void SwitchMode(WinoApplicationMode mode)
    {
        if (IsReleased) return;
        if (!_navigation.ChangeApplicationMode(mode)) Serilog.Log.Information("The {Mode} mode is not available.", mode);
    }

    private static string ModeTitle(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => "Wino " + Translator.KeyboardShortcuts_ModeCalendar,
        WinoApplicationMode.Contacts => "Wino " + Translator.ContactsPage_Title,
        WinoApplicationMode.Tasks => "Wino " + Translator.ToDoPage_Title,
        _ => AppActions.ApplicationName
    };

    private void UpdatePlaceholder()
    {
        if (IsReleased) return;
        Gtk.Widget? placeholder = null;
        if (_modes.ActiveMode is { } active && !_navigation.IsModeAvailable(active))
        {
            if (!_placeholders.TryGetValue(active, out placeholder))
            {
                placeholder = ModePlaceholderView.Create(active);
                _placeholders[active] = placeholder;
            }
        }
        _layout.SetPlaceholder(placeholder);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, null);
        _navigation.AttachRenderingHost(_reading);
        _navigation.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        Observe(RefreshWhatsNewAsync());
        return Task.CompletedTask;
    }

    // Mode activation navigates, so it follows OnPageLoaded as on the other heads.
    protected override Task LoadedAsync()
        => ActivateModeAsync(
            ShellModeActivator.StartMode(ViewModel.StatePersistenceService, _navigation.IsModeAvailable, ViewModel.PreferencesService.HiddenApplicationModes),
            new ShellModeActivationContext { IsInitialActivation = true });

    protected override async Task DeactivateAsync()
    {
        _navigation.DetachRenderingHost(_reading);
        _layout.StateChanged -= OnLayoutStateChanged;
        ObserveProvider(null);
        ObserveTitle(null);
        ViewModel.StatePersistenceService.PropertyChanged -= _stateChanged;
        ViewModel.PreferencesService.PreferenceChanged -= _preferenceChanged;
        if (_briefing is not null) _briefing.UnseenItemsChanged -= _briefingChanged;
        _header.Dispose();
        _switcher.ModeSelected -= SwitchMode;
        _switcher.Dispose();
        _sidebar.Dispose();
        await ClearContentAsync();
        await _pane.ReleaseAsync();
        _layout.Dispose();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        ViewModel.ShutdownProviders();
    }

    private async Task ReleaseContentAsync(IWinoView view)
    {
        try { await view.ReleaseAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void WireHeader()
    {
        _header.BackRequested += GoBack;
        _header.SidebarToggleRequested += _layout.ToggleSidebar;
        _header.SynchronizeRequested += Synchronize;
        _header.ModeRequested += SwitchMode;
        _header.WhatsNewRequested += ShowWhatsNew;
        _header.AccountRequested += OpenAccount;

        Bind.Bind(nameof(WinoAppShellViewModel.CurrentMode), static vm => vm.CurrentMode, mode =>
        {
            _switcher.Selected = mode;
            _sidebar.SetTitle(ModeTitle(mode));
            UpdateModes();
            UpdateTitle();
            UpdatePlaceholder();
        });
        Bind.Bind(nameof(WinoAppShellViewModel.ModeVisibility), static vm => vm.ModeVisibility, visibility =>
        {
            _switcher.SetVisibleModes(visibility?.VisibleModes);
            UpdateModes();
        });
        Bind.Bind(nameof(WinoAppShellViewModel.CurrentProvider), static vm => vm.CurrentProvider, ObserveProvider);
        Bind.Bind(nameof(WinoAppShellViewModel.SelectedMenuItem), static vm => vm.SelectedMenuItem, item =>
        {
            ObserveTitle(item as INotifyPropertyChanged);
            UpdateTitle();
        });
        Bind.Bind(nameof(WinoAppShellViewModel.IsRelayFallbackActive), static vm => vm.IsRelayFallbackActive, active =>
            _header.SetSynchronizationAttention(active ? $"{Translator.Relay_FallbackFlyoutTitle}. {Translator.Relay_FallbackFlyoutMessage}" : null));

        ViewModel.StatePersistenceService.PropertyChanged += _stateChanged;
        ViewModel.PreferencesService.PreferenceChanged += _preferenceChanged;
        if (_briefing is not null) _briefing.UnseenItemsChanged += _briefingChanged;
        ApplyBriefing();
        ApplyAccount();

        var backButton = Gtk.GestureClick.New();
        backButton.SetButton(8);
        Bindings.Own(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(
            h => backButton.OnPressed += h, h => backButton.OnPressed -= h, (_, _) => GoBack()));
        _layout.Root.AddController(backButton);
    }

    private void OnLayoutStateChanged()
    {
        if (IsReleased) return;
        _header.SetSidebarState(_layout.IsSidebarVisible);
        _header.SetWidthClass(_layout.WidthClass);
        RefreshBack();
    }

    private void RefreshBack()
        => _header.SetBackVisible((_layout.IsStacked && _layout.ShowsDetail) || CanGoBackToPage());

    // Back across folders would show the previous folder while the sidebar and the title keep the current one: no
    // head moves the folder selection on back, so the shell offers back only to pages that are not a folder.
    private bool CanGoBackToPage() => _navigation.CanGoBackToPage() && _navigation.BackPage is not WinoPage.MailListPage;

    private async Task RefreshBackAfterAsync(Task navigation)
    {
        await navigation;
        RunOnUI(RefreshBack);
    }

    private void GoBack()
    {
        if (IsReleased) return;
        if (_layout.IsStacked && _layout.ShowsDetail)
        {
            Messenger.Send(new ClearMailSelectionsRequested());
            ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
            RefreshBack();
            return;
        }
        if (CanGoBackToPage()) Observe(GoBackAsync());
    }

    private async Task GoBackAsync()
    {
        await _navigation.GoBackAsync();
        if (!IsReleased) RefreshBack();
    }

    private void UpdateModes() => _header.SetModes(ViewModel.CurrentMode, ViewModel.ModeVisibility?.VisibleModes);

    private void ObserveProvider(IShellMenuProvider? provider)
    {
        if (_provider is not null) _provider.PropertyChanged -= _providerChanged;
        _provider = provider;
        if (provider is not null) provider.PropertyChanged += _providerChanged;
        if (!IsReleased) ApplySynchronization();
    }

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IShellMenuProvider.IsSynchronizationSupported) or nameof(IShellMenuProvider.CanSynchronize)
            or nameof(IShellMenuProvider.SynchronizationState) or nameof(IShellMenuProvider.SynchronizationDescription) or nameof(IShellMenuProvider.SynchronizationToolTip))) return;
        RunOnUI(ApplySynchronization);
    }

    private void ApplySynchronization()
    {
        if (IsReleased) return;
        if (_provider is not { } provider)
        {
            _header.SetSynchronization(false, false, false, null);
            return;
        }
        var state = provider.SynchronizationState;
        var tip = state.IsSynchronizing && !string.IsNullOrWhiteSpace(provider.SynchronizationDescription)
            ? provider.SynchronizationDescription
            : provider.SynchronizationToolTip;
        _header.SetSynchronization(provider.IsSynchronizationSupported, provider.CanSynchronize || state.IsSynchronizing, state.IsSynchronizing, tip);
    }

    private void Synchronize()
    {
        if (_provider is { CanSynchronize: true } provider) Observe(provider.SynchronizeAsync());
    }

    private void ObserveTitle(INotifyPropertyChanged? source)
    {
        if (_titleSource is not null) _titleSource.PropertyChanged -= _titleSourceChanged;
        _titleSource = source;
        if (source is not null) source.PropertyChanged += _titleSourceChanged;
    }

    private void OnTitleSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IBaseFolderMenuItem.UnreadItemCount) or nameof(IBaseFolderMenuItem.FolderName))) return;
        RunOnUI(UpdateTitle);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case null or "":
                RunOnUI(() => { UpdateTitle(); RefreshBack(); });
                break;
            case nameof(IStatePersistanceService.CoreWindowTitle) or nameof(IStatePersistanceService.AppModeTitle):
                RunOnUI(UpdateTitle);
                break;
            case nameof(IStatePersistanceService.IsReadingMail) or nameof(IStatePersistanceService.IsReaderNarrowed) or nameof(IStatePersistanceService.IsEventDetailsVisible):
                RunOnUI(RefreshBack);
                break;
        }
    }

    private void OnPreferenceChanged(object? sender, string name)
    {
        if (name == nameof(IPreferencesService.IsWinoAccountButtonHidden)) RunOnUI(ApplyAccount);
    }

    /// <summary>In Mail the selected folder with its account and unread count; in the other modes the mode title and what the page reports.</summary>
    private void UpdateTitle()
    {
        if (IsReleased) return;
        var state = ViewModel.StatePersistenceService;
        string title;
        string subtitle;
        if (_modes.ActiveMode is WinoApplicationMode.Mail or null && ViewModel.SelectedMenuItem is IBaseFolderMenuItem folder)
        {
            title = folder.FolderName ?? string.Empty;
            var owner = folder is IFolderMenuItem { ParentAccount: { } account }
                ? (string.IsNullOrWhiteSpace(account.Address) ? account.Name : account.Address)
                : folder.AssignedAccountName;
            var unread = folder.ShowUnreadCount && folder.UnreadItemCount > 0 ? string.Format(Translator.UnreadBadges_Folders_UnreadFormat, folder.UnreadItemCount) : null;
            subtitle = string.Join(" · ", new[] { owner, unread }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        else
        {
            title = state.AppModeTitle;
            subtitle = state.CoreWindowTitle ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(title)) title = AppActions.ApplicationName;
        _header.SetTitle(title, subtitle);
        _navigation.MainWindow?.Window.SetTitle(title);
    }

    private async Task RefreshWhatsNewAsync()
    {
        var service = _services.GetService<IWhatsNewService>();
        var show = service is not null && _whatsNew is not null && await service.ShouldShowShellEntryAsync();
        RunOnUI(() =>
        {
            if (IsReleased) return;
            _header.SetWhatsNewVisible(show);
            AppActions.SetEnabled(Gio.Application.GetDefault(), AppActions.WhatsNew, _whatsNew is not null);
        });
    }

    private async void ShowWhatsNew()
    {
        try
        {
            if (_whatsNew is null) return;
            await _whatsNew.ShowAsync();
            _services.GetService<IWhatsNewService>()?.MarkOpenedForCurrentVersion();
            if (!IsReleased) _header.SetWhatsNewVisible(false);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void OpenAccount(Gtk.Widget anchor) => _account?.Show(anchor);

    private void ApplyAccount()
    {
        if (!IsReleased) _header.SetAccountVisible(_account is not null && !ViewModel.PreferencesService.IsWinoAccountButtonHidden);
    }

    private void ApplyBriefing()
    {
        if (!IsReleased) _header.SetBriefing(_briefing is { IsAvailable: true }, _briefing is { HasUnseenItems: true }, false);
    }

    /// <summary>Tells the layout whether the reading pane has a page, so the stacked layout shows the reader or the list.</summary>
    private sealed class ShellReadingPane(ReadingPaneHost inner, ShellLayout layout) : IRenderingFrameHost
    {
        public Task ShowAsync(WinoPage page, object? parameter, Func<IWinoView> create)
        {
            layout.ShowDetail(page != WinoPage.IdlePage);
            return inner.ShowAsync(page, parameter, create);
        }

        public Task ClearAsync()
        {
            layout.ShowDetail(false);
            return inner.ClearAsync();
        }
    }
}
