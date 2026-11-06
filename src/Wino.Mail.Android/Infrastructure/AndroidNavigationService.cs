using FragmentTransaction = AndroidX.Fragment.App.FragmentTransaction;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Android.Views.Settings;
using Wino.Mail.Android.Views.Shell;
using Wino.Messaging.Client.Navigation;
using Wino.Mail.ViewModels.Navigation;
using Wino.Shell.ViewModels;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The Android host of the shared <see cref="NavigationCoordinator"/>, which owns routes, history, back confirmation and
/// the account lifecycle reactions (UIKitNavigationService and the Mac AppKitNavigationService). This class keeps the
/// routed pages as <see cref="PageEntry"/> layers that live in the application container, independent of any activity:
/// <list type="bullet">
/// <item>the main content (onboarding pages, or the shell),</item>
/// <item>the shell's content page,</item>
/// <item>one back stack per application mode for pushed and rendering-frame pages,</item>
/// <item>the secondary onboarding page (Settings › Add account while the shell is up),</item>
/// <item>the Settings stack, shown by one <see cref="SettingsHostFragment"/>: the root list and the top page on compact
/// widths one at a time, side by side (list and detail) on medium and expanded widths.</item>
/// </list>
/// The top-most entry is shown in the activity's fragment container. While the shell is up that is the shell
/// (<see cref="ShellFragment"/>), which places the shell's content page and, on medium and expanded widths, the mode's
/// rendering-frame page (the reader) in its own panes; on compact widths a rendering-frame page and every pushed page
/// cover the shell full screen. When the activity is recreated (rotation, dark mode, resize) <see cref="AttachActivity"/>
/// shows the same entries in new fragments, so ViewModel state survives.
/// Back goes through the activity's OnBackPressedDispatcher (<see cref="TryHandleBack"/>); its callback is enabled only
/// while <see cref="CanHandleBack"/>, so the system's predictive back-to-home animation runs otherwise.
/// </summary>
public sealed class AndroidNavigationService : INavigationService, INavigationHost, IDisposable, IRecipient<SettingsRootNavigationRequested>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly AndroidPageRegistry _registry;
    private readonly NavigationCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<WinoApplicationMode, List<PageEntry>> _modeStacks = new();
    private readonly List<PageEntry> _settings = [];
    private PageEntry? _main;
    private PageEntry? _shellContent;
    private PageEntry? _secondary;
    private PageEntry? _settingsHost;
    private bool _shellShown;
    private WinoApplicationMode _mode = WinoApplicationMode.Mail;
    private MainActivity? _activity;
    private IWindowSizeClassService? _sizes;

    public AndroidNavigationService(IServiceProvider services, IDispatcher dispatcher, AndroidPageRegistry registry)
    {
        _services = services;
        _dispatcher = dispatcher;
        _registry = registry;
        _coordinator = new NavigationCoordinator(this, services, dispatcher);
        WeakReferenceMessenger.Default.Register<SettingsRootNavigationRequested>(this);
    }

    /// <summary>Raised on the UI thread whenever the pages change, so the activity can update its back callback.</summary>
    public event EventHandler? BackStateChanged;

    /// <summary>Raised on the UI thread when the shell's content page or the mode's rendering-frame page changed.</summary>
    public event EventHandler? ShellPagesChanged;

    /// <summary>Raised on the UI thread when the Settings stack changed (the settings host re-places its panes).</summary>
    public event EventHandler? SettingsPagesChanged;

    /// <summary>The Settings stack, bottom first: the root list, then the pages opened from it.</summary>
    public IReadOnlyList<PageEntry> SettingsEntries
    {
        get
        {
            lock (_sync) return [.. _settings];
        }
    }

    /// <summary>True when Settings shows the root list beside the page (medium and expanded widths).</summary>
    public bool ShowsSettingsDetailPane => Sizes.WidthClass != WindowWidthClass.Compact;

    /// <summary>The shell's behaviour while the shell is up (modes, menu, synchronization).</summary>
    public AndroidShellController? Shell { get; private set; }

    /// <summary>The shell's content page (the folder list in Mail).</summary>
    public PageEntry? ShellContentEntry => _shellContent;

    /// <summary>The current mode's rendering-frame page (the reader), wherever it is shown.</summary>
    public PageEntry? RenderingEntry
    {
        get
        {
            lock (_sync) return CurrentModeStack().LastOrDefault(static entry => entry.Host == AndroidPageHost.RenderingFrame);
        }
    }

    /// <summary>True when the shell shows rendering-frame pages in its reader pane (medium and expanded widths).</summary>
    public bool ShowsReaderPane => Sizes.WidthClass != WindowWidthClass.Compact;

    private IWindowSizeClassService Sizes => _sizes ??= _services.GetRequiredService<IWindowSizeClassService>();

    /// <summary>The application mode whose back stack receives pushed pages.</summary>
    public WinoApplicationMode ActiveMode => _mode;

    public bool CanQuit => _coordinator.CanQuit;
    public bool IsShellReady => _coordinator.IsShellReady;

    /// <summary>True when back has somewhere to go inside the app.</summary>
    public bool CanHandleBack
    {
        get
        {
            lock (_sync)
            {
                if (_settings.Count > 0 || _secondary is not null || CurrentModeStack().Count > 0) return true;
                // Material top-level destinations: back from Calendar, Contacts or To Do returns to Mail, the start mode.
                if (_shellShown && _mode != WinoApplicationMode.Mail) return true;
            }
            // The first shell page has nothing behind it: back leaves the app (the coordinator would empty the mode).
            // Back never crosses into another mode's page: the shell would keep the old mode around the new content.
            return _coordinator.CanGoBackToPage() && (!_shellShown || _coordinator.BackPage is not { } page || ModeOfPage(page) == _mode);
        }
    }

    /// <summary>The mode whose root page <paramref name="page"/> is; every other shell page belongs to Mail.</summary>
    private static WinoApplicationMode ModeOfPage(WinoPage page) => page switch
    {
        WinoPage.CalendarPage => WinoApplicationMode.Calendar,
        WinoPage.ContactsPage => WinoApplicationMode.Contacts,
        WinoPage.ToDoPage => WinoApplicationMode.Tasks,
        _ => WinoApplicationMode.Mail
    };

    #region Activity

    /// <summary>Shows the current page in <paramref name="activity"/>, which was just created. Main thread.</summary>
    public void AttachActivity(MainActivity activity)
    {
        _activity = activity;
        ShowTop(force: true);
    }

    /// <summary>Forgets <paramref name="activity"/> when it is destroyed. Main thread.</summary>
    public void DetachActivity(MainActivity activity)
    {
        if (ReferenceEquals(_activity, activity)) _activity = null;
    }

    /// <summary>The system back gesture or button. Main thread.</summary>
    public bool TryHandleBack()
    {
        PageEntry? popped = null;
        lock (_sync)
        {
            if (_settings.Count == 0 && _secondary is null && CurrentModeStack() is { Count: > 0 } stack)
            {
                popped = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
        }
        if (popped is not null)
        {
            ShowTop();
            BackStateChanged?.Invoke(this, EventArgs.Empty);
            ShellPagesChanged?.Invoke(this, EventArgs.Empty);
            _ = ReleaseQuietlyAsync(popped);
            return true;
        }
        if (!CanHandleBack) return false;
        bool toStartMode;
        lock (_sync) toStartMode = _settings.Count == 0 && _secondary is null && _shellShown && _mode != WinoApplicationMode.Mail;
        if (toStartMode)
        {
            _coordinator.ChangeApplicationMode(WinoApplicationMode.Mail);
            return true;
        }
        _coordinator.GoBack();
        return true;
    }

    #endregion

    #region INavigationService

    public Type? GetPageType(WinoPage page) => _registry.TryGet(page, out var route) ? route.FragmentType : null;

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
        => _coordinator.Navigate(page, parameter, frame, transition);

    public Task NavigateAsync(WinoPage page, object? parameter = null) => _coordinator.NavigateAsync(page, parameter);
    public Task ShowShellAsync() => _coordinator.ShowShellAsync();
    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => _coordinator.EnsureShellAsync(mode, parameter);
    public bool ChangeApplicationMode(WinoApplicationMode mode) => _coordinator.ChangeApplicationMode(mode);
    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.ChangeApplicationMode(mode, activationContext);
    public bool IsModeAvailable(WinoApplicationMode mode) => _coordinator.IsModeAvailable(mode);
    public bool ParkShell() => _coordinator.ParkShell();
    public bool RestoreShell(WinoApplicationMode mode) => _coordinator.RestoreShell(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.RestoreShell(mode, activationContext);
    public bool CanGoBack() => _coordinator.CanGoBack();
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBack(slideEffect);
    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBackAsync(slideEffect);
    public void SetNavigationResult(NavigationResult result) => _coordinator.SetNavigationResult(result);

    /// <summary>Opens Settings on <paramref name="page"/> (activation and notification returns).</summary>
    public Task ShowSettingsPageAsync(WinoPage? page, object? parameter) => ((INavigationHost)this).ShowSettingsAsync(page, parameter);

    public Task StopAsync() => _coordinator.StopAsync();

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _coordinator.Dispose();
    }

    #endregion

    #region INavigationHost

    bool INavigationHost.TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind)
    {
        if (!_registry.TryGet(page, out var route))
        {
            hostKind = default;
            return false;
        }
        hostKind = AndroidPageRegistry.ToHostKind(route.Host);
        return true;
    }

    bool INavigationHost.IsShellShown => _shellShown;
    bool INavigationHost.HasMainContent => _main is not null;
    bool INavigationHost.MainContentHasPendingWork => _main is { HasPendingWork: true };
    object? INavigationHost.ShellContentViewModel => _shellContent?.ViewModel;
    bool INavigationHost.IsSecondaryWindowShown => _secondary is not null;
    bool INavigationHost.IsSettingsAvailable => _registry.TryGet(WinoPage.SettingsPage, out _);

    async Task INavigationHost.ShowInRenderingFrameAsync(WinoPage page, object? parameter)
    {
        var entry = CreateEntry(page, parameter);
        var replaced = new List<PageEntry>();
        lock (_sync)
        {
            var stack = CurrentModeStack();
            // One rendering-frame page per mode (the reader column): a new one replaces it and whatever was pushed over it.
            if (entry.Host == AndroidPageHost.RenderingFrame && stack.FindIndex(static existing => existing.Host == AndroidPageHost.RenderingFrame) is >= 0 and var index)
            {
                replaced.AddRange(stack.Skip(index));
                stack.RemoveRange(index, stack.Count - index);
            }
            stack.Add(entry);
        }
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
        await ReleaseAllAsync(replaced);
        await entry.ActivateAsync(NavigationMode.New);
    }

    /// <summary>
    /// Shows a mode's detail view that has no Windows page of its own (the contact card, the task detail drawer) as the
    /// current mode's rendering-frame page: in the reader pane on medium and expanded widths, full screen over the shell
    /// on compact widths, popped by back. A detail with the same <paramref name="key"/> already on screen stays (it
    /// follows its ViewModel's selection). <paramref name="released"/> runs once when the detail leaves (back, close,
    /// replaced, mode content changed). Main thread or any thread.
    /// </summary>
    public async Task ShowDetailAsync(string key, Func<WinoFragment> createFragment, Action? released = null)
    {
        var entry = new PageEntry(WinoPage.None, null, null, createFragment, _dispatcher) { Host = AndroidPageHost.RenderingFrame };
        entry.State[DetailKey] = key;
        if (released is not null) entry.State[DetailKey + ".released"] = new ActionDisposable(released);
        var replaced = new List<PageEntry>();
        lock (_sync)
        {
            var stack = CurrentModeStack();
            var index = stack.FindIndex(static existing => existing.Host == AndroidPageHost.RenderingFrame);
            if (index >= 0 && stack[index].State.TryGetValue(DetailKey, out var shown) && Equals(shown, key)) return;
            if (index >= 0)
            {
                replaced.AddRange(stack.Skip(index));
                stack.RemoveRange(index, stack.Count - index);
            }
            stack.Add(entry);
        }
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
        await ReleaseAllAsync(replaced);
    }

    /// <summary>The <see cref="ShowDetailAsync"/> key of the current mode's rendering-frame page, if it is a detail.</summary>
    public string? CurrentDetailKey
    {
        get
        {
            lock (_sync)
                return CurrentModeStack().LastOrDefault(static entry => entry.Host == AndroidPageHost.RenderingFrame) is { } entry
                    && entry.State.TryGetValue(DetailKey, out var key) ? key as string : null;
        }
    }

    private const string DetailKey = "wino.detail";

    /// <summary>Closes the current mode's rendering-frame page (and anything pushed over it), as the Windows frame disposal does.</summary>
    public async Task CloseRenderingFrameAsync()
    {
        var closed = new List<PageEntry>();
        lock (_sync)
        {
            var stack = CurrentModeStack();
            if (stack.FindIndex(static existing => existing.Host == AndroidPageHost.RenderingFrame) is >= 0 and var index)
            {
                closed.AddRange(stack.Skip(index));
                stack.RemoveRange(index, stack.Count - index);
            }
        }
        if (closed.Count == 0) return;
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
        await ReleaseAllAsync(closed);
    }

    async Task INavigationHost.SetShellContentAsync(WinoPage page, object? parameter)
    {
        if (!_shellShown) return;
        // A page that takes new parameters in place (the calendar's date navigation) keeps its entry and view.
        if (_shellContent is { IsReleased: false, Renavigate: not null } current && current.Page == page)
        {
            List<PageEntry> stale;
            lock (_sync)
            {
                stale = [.. CurrentModeStack()];
                CurrentModeStack().Clear();
            }
            await ShowTopAsync();
            await RaiseShellPagesChangedAsync();
            await ReleaseAllAsync(stale);
            await current.RenavigateAsync(parameter);
            return;
        }
        var entry = CreateEntry(page, parameter);
        PageEntry? previous;
        List<PageEntry> pushed;
        lock (_sync)
        {
            previous = _shellContent;
            _shellContent = entry;
            // New content starts the mode's push stack again.
            pushed = [.. CurrentModeStack()];
            CurrentModeStack().Clear();
        }
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
        await ReleaseAllAsync(pushed);
        if (previous is not null) await previous.ReleaseAsync();
        await entry.ActivateAsync(NavigationMode.New);
        // Back restores a folder without going through the pane, so the pane would keep the folder that was left.
        if (parameter is NavigateMailFolderEventArgs { BaseFolderMenuItem: { } folder } && Shell is { } shell)
            await _dispatcher.ExecuteOnUIThread(() => shell.ViewModel.SelectedMenuItem = folder);
        PostBackStateChanged();
    }

    Task INavigationHost.ClearShellContentAsync() => ClearShellContentAsync();

    /// <summary>
    /// The coordinator records its history after the host call returns, so the back state is raised from the next main
    /// loop turn; the activity's back callback (and predictive back) then matches the history in every orientation.
    /// </summary>
    private void PostBackStateChanged()
        => _mainHandler.Post(() => BackStateChanged?.Invoke(this, EventArgs.Empty));

    private readonly global::Android.OS.Handler _mainHandler = new(global::Android.OS.Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));

    /// <summary>Releases the shell's content page and the mode's pushed pages (a mode switch, or back to no folder).</summary>
    internal async Task ClearShellContentAsync()
    {
        PageEntry? previous;
        List<PageEntry> pushed;
        lock (_sync)
        {
            previous = _shellContent;
            _shellContent = null;
            pushed = [.. CurrentModeStack()];
            CurrentModeStack().Clear();
        }
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
        await ReleaseAllAsync(pushed);
        if (previous is not null) await previous.ReleaseAsync();
        PostBackStateChanged();
    }

    async Task INavigationHost.ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        var entry = CreateEntry(page, parameter);
        var released = new List<PageEntry>();
        AndroidShellController? shell = null;
        lock (_sync)
        {
            if (_main is not null) released.Add(_main);
            _main = entry;
            // Leaving the shell drops everything it owned.
            if (_shellShown)
            {
                released.AddRange(DropShellLayers());
                shell = Shell;
                Shell = null;
            }
            _shellShown = false;
        }
        await ShowTopAsync();
        committed();
        await ReleaseAllAsync(released);
        if (shell is not null) await shell.ReleaseAsync();
        await entry.ActivateAsync(mode);
        await RaiseBackStateChangedAsync();
    }

    async Task INavigationHost.ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        var entry = CreateEntry(page, parameter);
        PageEntry? previous;
        lock (_sync)
        {
            previous = _secondary;
            _secondary = entry;
        }
        await ShowTopAsync();
        committed();
        if (previous is not null) await previous.ReleaseAsync();
        await entry.ActivateAsync(mode);
        await RaiseBackStateChangedAsync();
    }

    async Task INavigationHost.CloseSecondaryWindowAsync(bool closedByUser)
    {
        PageEntry? previous;
        lock (_sync)
        {
            previous = _secondary;
            _secondary = null;
        }
        if (previous is null) return;
        await ShowTopAsync();
        await previous.ReleaseAsync();
    }

    /// <summary>
    /// Shows the shell (<see cref="ShellFragment"/>) as the main content. Its behaviour lives in an
    /// <see cref="AndroidShellController"/> beside the entry, so it survives activity recreation; the returned activation
    /// starts the persisted mode like the Windows shell.
    /// </summary>
    async Task<Func<Task>> INavigationHost.PresentShellAsync(Action committed)
    {
        var controller = new AndroidShellController(_services.GetRequiredService<WinoAppShellViewModel>(), this,
            _services.GetRequiredService<IStatePersistanceService>(), _services, _dispatcher);
        var shell = new PageEntry(WinoPage.None, null, null, static () => new ShellFragment(), _dispatcher);
        var released = new List<PageEntry>();
        AndroidShellController? previousShell;
        lock (_sync)
        {
            if (_main is not null) released.Add(_main);
            released.AddRange(DropShellLayers());
            _main = shell;
            previousShell = Shell;
            Shell = controller;
            _shellShown = true;
        }
        await ShowTopAsync();
        committed();
        await ReleaseAllAsync(released);
        if (previousShell is not null) await previousShell.ReleaseAsync();
        return async () =>
        {
            await RaiseBackStateChangedAsync();
            await controller.ActivateAsync();
        };
    }

    // One activity: the shell is in front whenever it exists.
    Task INavigationHost.BringShellToFrontAsync() => Task.CompletedTask;

    async Task INavigationHost.ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
    {
        if (!_shellShown || Shell is not { } shell) return;
        await shell.ActivateModeAsync(mode, activationContext);
    }

    /// <summary>The shell switched to <paramref name="mode"/>: its back stack becomes current. Called by the shell controller.</summary>
    internal async Task SetActiveModeAsync(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings) return;
        lock (_sync) _mode = mode;
        await ShowTopAsync();
        await RaiseShellPagesChangedAsync();
    }

    async Task INavigationHost.DismissShellSurfacesAsync()
    {
        List<PageEntry> released;
        lock (_sync)
        {
            released = [.. _settings];
            _settings.Clear();
            foreach (var stack in _modeStacks.Values)
            {
                released.AddRange(stack);
                stack.Clear();
            }
        }
        await ShowTopAsync();
        await ReleaseAllAsync(released);
    }

    async Task INavigationHost.ReleaseMainContentAsync()
    {
        var released = new List<PageEntry>();
        AndroidShellController? shell;
        lock (_sync)
        {
            if (_main is not null) released.Add(_main);
            released.AddRange(DropShellLayers());
            if (_secondary is not null) released.Add(_secondary);
            _main = null;
            _secondary = null;
            _shellShown = false;
            shell = Shell;
            Shell = null;
        }
        await ShowTopAsync();
        await ReleaseAllAsync(released);
        if (shell is not null) await shell.ReleaseAsync();
    }

    /// <summary>
    /// Opens Settings, or a page inside it. Settings always has the root list at the bottom of its stack, so a request
    /// for a page (activation, an account's settings from the shell menu) opens over the list; opening the root again
    /// while Settings is up does nothing.
    /// </summary>
    async Task INavigationHost.ShowSettingsAsync(WinoPage? page, object? parameter)
    {
        var target = page is null or WinoPage.SettingOptionsPage ? WinoPage.SettingsPage : page.Value;
        if (!_registry.TryGet(target, out _)) return;
        var added = new List<PageEntry>();
        lock (_sync)
        {
            if (_settings.Count == 0) added.Add(CreateEntry(WinoPage.SettingsPage, null));
            if (target != WinoPage.SettingsPage) added.Add(CreateEntry(target, parameter));
            _settings.AddRange(added);
        }
        if (added.Count == 0) return;
        await ShowTopAsync();
        await RaiseSettingsPagesChangedAsync();
        foreach (var entry in added) await entry.ActivateAsync(NavigationMode.New);
    }

    /// <summary>
    /// Shows <paramref name="page"/> right over the root list (a root list row on medium and expanded widths, the Windows
    /// SettingsRootNavigationRequested): pages opened from the previous row are released.
    /// </summary>
    public async Task SelectSettingsRootPageAsync(WinoPage page, object? parameter = null)
    {
        if (!_registry.TryGet(page, out _)) return;
        var entry = CreateEntry(page, parameter);
        List<PageEntry> released;
        lock (_sync)
        {
            if (_settings.Count == 0) return;
            released = _settings.Skip(1).ToList();
            _settings.RemoveRange(1, _settings.Count - 1);
            _settings.Add(entry);
        }
        await ShowTopAsync();
        await RaiseSettingsPagesChangedAsync();
        await ReleaseAllAsync(released);
        await entry.ActivateAsync(NavigationMode.New);
    }

    /// <summary>
    /// Pushes the generic setting detail page (<see cref="WinoSettingDetailFragment"/>) for the setting at
    /// <paramref name="path"/> on <paramref name="owner"/>'s page. It is an ordinary Settings stack entry without a
    /// ViewModel: back pops it, and its title is the setting's header.
    /// </summary>
    public async Task PushSettingsDetailAsync(PageEntry owner, string[] path)
    {
        var entry = new PageEntry(WinoPage.None, null, null, static () => new WinoSettingDetailFragment(), _dispatcher) { Host = AndroidPageHost.Settings };
        WinoSettingDetailFragment.Configure(entry, owner, path);
        lock (_sync)
        {
            if (!_settings.Contains(owner)) return;
            _settings.Add(entry);
        }
        await ShowTopAsync();
        await RaiseSettingsPagesChangedAsync();
    }

    /// <summary>A page asked for a root list row (Windows SettingsPage receives it): the row's page replaces the stack above the list.</summary>
    public void Receive(SettingsRootNavigationRequested message)
        => _ = ObserveSettingsAsync(SelectSettingsRootPageAsync(message.PageType));

    /// <summary>
    /// Back inside Settings pops its stack; the last page closes Settings. With the list beside the page, back from a
    /// list row's page closes Settings too (the list is already on screen). A page with unsaved changes
    /// (<see cref="IConfirmBackNavigation"/>, the theme editor) is asked first.
    /// </summary>
    bool INavigationHost.TrySettingsGoBack()
    {
        PageEntry top;
        bool closeAll;
        lock (_sync)
        {
            if (_settings.Count == 0) return false;
            top = _settings[^1];
            closeAll = _settings.Count == 1 || (_settings.Count == 2 && ShowsSettingsDetailPane);
        }
        _ = ObserveSettingsAsync(SettingsBackAsync(top, closeAll));
        return true;
    }

    private async Task SettingsBackAsync(PageEntry top, bool closeAll)
    {
        if (top.ViewModel is IConfirmBackNavigation confirm && !await confirm.CanNavigateBackAsync()) return;
        if (closeAll)
        {
            await CloseSettingsAsync();
            return;
        }
        lock (_sync)
        {
            if (_settings.Count == 0 || !ReferenceEquals(_settings[^1], top)) return;
            _settings.RemoveAt(_settings.Count - 1);
        }
        await ShowTopAsync();
        await RaiseSettingsPagesChangedAsync();
        await ReleaseQuietlyAsync(top);
    }

    private static async Task ObserveSettingsAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Settings navigation failed."); }
    }

    Task INavigationHost.CloseSettingsAsync() => CloseSettingsAsync();

    Task INavigationHost.StopSettingsAsync() => CloseSettingsAsync();

    #endregion

    private async Task CloseSettingsAsync()
    {
        List<PageEntry> released;
        lock (_sync)
        {
            released = [.. _settings];
            _settings.Clear();
        }
        if (released.Count == 0) return;
        await ShowTopAsync();
        await RaiseSettingsPagesChangedAsync();
        await ReleaseAllAsync(released);
    }

    /// <summary>Removes the shell's content, mode stacks and Settings. Call under the lock.</summary>
    private List<PageEntry> DropShellLayers()
    {
        var dropped = new List<PageEntry>();
        if (_shellContent is not null) dropped.Add(_shellContent);
        _shellContent = null;
        foreach (var stack in _modeStacks.Values)
        {
            dropped.AddRange(stack);
            stack.Clear();
        }
        dropped.AddRange(_settings);
        _settings.Clear();
        return dropped;
    }

    private List<PageEntry> CurrentModeStack()
    {
        if (!_modeStacks.TryGetValue(_mode, out var stack))
        {
            stack = [];
            _modeStacks[_mode] = stack;
        }
        return stack;
    }

    private PageEntry? TopEntry()
    {
        lock (_sync)
        {
            // The secondary surface covers Settings, matching the coordinator's back order (Settings > Add account opens
            // provider selection there).
            if (_secondary is not null) return _secondary;
            // One host shows the whole Settings stack (list and page side by side when there is room).
            if (_settings.Count > 0) return _settingsHost ??= new PageEntry(WinoPage.None, null, null, static () => new SettingsHostFragment(), _dispatcher) { Host = AndroidPageHost.Settings };
            // Pushed pages always cover the shell; the reader does on compact widths, else the shell shows it in its pane.
            if (CurrentModeStack() is { Count: > 0 } stack && (stack[^1].Host != AndroidPageHost.RenderingFrame || !_shellShown || !ShowsReaderPane))
                return stack[^1];
            return _main;
        }
    }

    private PageEntry CreateEntry(WinoPage page, object? parameter)
    {
        if (!_registry.TryGet(page, out var route))
            throw new NotSupportedException($"The Android route {page} has not been implemented.");
        var viewModel = route.CreateViewModel?.Invoke(_services);
        return new PageEntry(page, parameter, viewModel, route.CreateFragment, _dispatcher)
        {
            Host = route.Host,
            Preparing = route.Preparing,
            Activated = route.Activated,
            Initialize = route.Initialize,
            Deactivate = route.Deactivate,
            Renavigate = route.Renavigate
        };
    }

    private async Task ShowTopAsync()
    {
        await _dispatcher.ExecuteOnUIThread(() => ShowTop());
        await RaiseBackStateChangedAsync();
    }

    /// <summary>Puts the top entry in the activity's fragment container. Main thread.</summary>
    private void ShowTop(bool force = false)
    {
        if (_activity is not { IsDestroyed: false } activity) return;
        var manager = activity.SupportFragmentManager;
        if (manager.IsDestroyed) return;
        var top = TopEntry();
        var current = manager.FindFragmentById(MainActivity.ContentContainerId);
        if (top is null)
        {
            if (current is not null) manager.BeginTransaction().Remove(current).CommitNowAllowingStateLoss();
            return;
        }
        if (!force && current is WinoFragment shown && ReferenceEquals(shown.Entry, top)) return;
        var fragment = top.CreateFragment();
        manager.BeginTransaction()
            .SetReorderingAllowed(true)
            .SetTransition(current is null ? FragmentTransaction.TransitNone : FragmentTransaction.TransitFragmentFade)
            .Replace(MainActivity.ContentContainerId, fragment)
            .CommitNowAllowingStateLoss();
    }

    private Task RaiseBackStateChangedAsync()
        => _dispatcher.ExecuteOnUIThread(() => BackStateChanged?.Invoke(this, EventArgs.Empty));

    private Task RaiseSettingsPagesChangedAsync()
        => _dispatcher.ExecuteOnUIThread(() => SettingsPagesChanged?.Invoke(this, EventArgs.Empty));

    private Task RaiseShellPagesChangedAsync()
        => _dispatcher.ExecuteOnUIThread(() => ShellPagesChanged?.Invoke(this, EventArgs.Empty));

    private static async Task ReleaseAllAsync(IEnumerable<PageEntry> entries)
    {
        foreach (var entry in entries) await ReleaseQuietlyAsync(entry);
    }

    private static async Task ReleaseQuietlyAsync(PageEntry entry)
    {
        try { await entry.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Releasing page {Page} failed.", entry.Page); }
    }
}
