using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Shell;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The shell: <see cref="ShellSidebarView"/> beside the content area that <see cref="ShellLayout"/> arranges, with the
/// reading pane beside pages that use it. Mail is the only mode of the proof of concept.
/// </summary>
internal sealed class ShellView : WinoView<WinoAppShellViewModel>
{
    private readonly GtkNavigationService _navigation;
    private readonly ShellModeActivator _modes;
    private readonly ReadingPaneHost _pane;
    private readonly ShellReadingPane _reading;
    private readonly ShellLayout _layout;
    private readonly ShellSidebarView _sidebar;
    private readonly Gtk.Button _back = Gtk.Button.NewFromIconName("go-previous-symbolic");
    private readonly Gtk.ToggleButton _sidebarToggle = Gtk.ToggleButton.New();
    private IWinoView? _content;
    private int _contentVersion;
    private bool _applyingHeader;

    public ShellView(WinoAppShellViewModel viewModel, WinoViewServices services, GtkNavigationService navigation, WindowStateStore store) : base(viewModel, services)
    {
        _navigation = navigation;
        _modes = new ShellModeActivator(ViewModel, ViewModel.StatePersistenceService, exception => Serilog.Log.Error(exception, "Shell mode activation failed."));
        _pane = new ReadingPaneHost(ReportError);
        _layout = new ShellLayout(ViewModel.PreferencesService, ViewModel.StatePersistenceService, store);
        _reading = new ShellReadingPane(_pane, _layout);
        _sidebar = new ShellSidebarView(ViewModel, Bind);
        _sidebar.ItemInvoked += _layout.DismissOverlaySidebar;
        _layout.SetSidebar(_sidebar.Root);
        _layout.ReadingPane = _pane.Root;
        _layout.SetHeader(BuildBasicHeader());
        _layout.StateChanged += RefreshHeader;
        RefreshHeader();
    }

    public override Gtk.Widget Root => _layout.Root;
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;
    public object? ContentViewModel { get; private set; }
    internal ShellLayout Layout => _layout;
    internal ShellSidebarView Sidebar => _sidebar;

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
        if (previous is not null) await ReleaseContentAsync(previous);
        if (version != _contentVersion) return;
        await view.ActivateAsync(NavigationMode.New, parameter);
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
        }
        if (previous is not null) await ReleaseContentAsync(previous);
    }

    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        if (IsReleased || mode != WinoApplicationMode.Mail) return;
        await _modes.ActivateAsync(mode, context, _navigation.ClearShellContentAsync, Dispatcher);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, null);
        _navigation.AttachRenderingHost(_reading);
        _navigation.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        return Task.CompletedTask;
    }

    // Mode activation navigates, so it follows OnPageLoaded as on the other heads.
    protected override Task LoadedAsync()
        => ActivateModeAsync(WinoApplicationMode.Mail, new ShellModeActivationContext { IsInitialActivation = true });

    protected override async Task DeactivateAsync()
    {
        _navigation.DetachRenderingHost(_reading);
        _layout.StateChanged -= RefreshHeader;
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

    private Gtk.Widget BuildBasicHeader()
    {
        WinoA11y.LabelWithTooltip(_back, Translator.Buttons_Back);
        _sidebarToggle.SetIconName("sidebar-show-symbolic");
        WinoA11y.LabelWithTooltip(_sidebarToggle, Translator.MacOSMenu_ToggleSidebar);
        Bindings.Own(GtkSignals.OnClicked(_back, () => ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame)));
        Bindings.Own(GtkSignals.OnToggled(_sidebarToggle, () =>
        {
            if (!_applyingHeader && _sidebarToggle.GetActive() != _layout.IsSidebarVisible) _layout.ToggleSidebar();
        }));

        var title = Adw.WindowTitle.New(ViewModel.StatePersistenceService.AppModeTitle ?? string.Empty, string.Empty);
        Bind.For(ViewModel.StatePersistenceService).Bind(nameof(ViewModel.StatePersistenceService.AppModeTitle),
            static state => state.AppModeTitle, text => title.SetTitle(text ?? string.Empty));

        var header = Adw.HeaderBar.New();
        header.PackStart(_back);
        header.PackStart(_sidebarToggle);
        header.SetTitleWidget(title);
        return header;
    }

    private void RefreshHeader()
    {
        if (IsReleased) return;
        _back.SetVisible(_layout.IsStacked && _layout.ShowsDetail);
        _applyingHeader = true;
        try { _sidebarToggle.SetActive(_layout.IsSidebarVisible); }
        finally { _applyingHeader = false; }
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
