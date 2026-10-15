using System.Windows.Input;
using Android.OS;
using Android.Views;
using Google.Android.Material.Button;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Presentation.Bindings;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// One routed page: its ViewModel and parameter, owned by <see cref="AndroidNavigationService"/> for as long as the
/// page is in a navigation stack. Fragments are only views over an entry: a configuration change (rotation, dark mode,
/// window resize) recreates the activity and its fragments, and the new fragment binds to the same entry, so the
/// ViewModel keeps its state and is not navigated to again.
/// </summary>
public sealed class PageEntry
{
    private readonly Func<WinoFragment> _createFragment;
    private Task _activation = Task.CompletedTask;
    private bool _activated;
    private bool _released;

    public PageEntry(WinoPage page, object? parameter, CoreBaseViewModel? viewModel, Func<WinoFragment> createFragment, IDispatcher dispatcher)
    {
        Page = page;
        Parameter = parameter;
        ViewModel = viewModel;
        Dispatcher = dispatcher;
        _createFragment = createFragment;
        if (viewModel is not null) viewModel.Dispatcher = dispatcher;
    }

    public WinoPage Page { get; }
    public object? Parameter { get; private set; }

    /// <summary>Where the page is shown (set from its route).</summary>
    public AndroidPageHost Host { get; init; } = AndroidPageHost.Root;

    /// <summary>Runs on the UI thread before OnNavigatedTo (the page's dispatchers and render bridges; from its route).</summary>
    public Action<PageEntry>? Preparing { get; init; }

    /// <summary>Runs after OnNavigatedTo and is part of the activation (the page's own navigation steps; from its route).</summary>
    public Func<PageEntry, Task>? Activated { get; init; }

    /// <summary>
    /// Replaces OnNavigatedTo when set: pages whose ViewModel offers an awaitable initialization (InitializeAsync,
    /// InitializeNavigationAsync) or must not run the Windows navigation work (iOS and Mac InitializeAsync overrides).
    /// </summary>
    public Func<PageEntry, NavigationMode, Task>? Initialize { get; init; }

    /// <summary>Replaces OnNavigatedFrom when set (iOS and Mac DeactivateAsync overrides).</summary>
    public Func<PageEntry, Task>? Deactivate { get; init; }

    /// <summary>
    /// Set for shell-content pages that take a new navigation parameter in place (the calendar's date navigation): the
    /// entry and its view stay, <see cref="Parameter"/> changes and this runs instead of a new activation.
    /// </summary>
    public Func<PageEntry, Task>? Renavigate { get; init; }

    /// <summary>Navigates this already shown entry again with <paramref name="parameter"/> (see <see cref="Renavigate"/>).</summary>
    internal async Task RenavigateAsync(object? parameter)
    {
        if (_released || Renavigate is not { } renavigate) return;
        await Dispatcher.ExecuteOnUIThread(() => Parameter = parameter);
        try { await _activation; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Page {Page} failed while it was being shown.", Page); }
        Task pending = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() => pending = _released ? Task.CompletedTask : renavigate(this));
        await pending;
    }

    /// <summary>
    /// State that belongs to the routed page rather than to one of its views (list scroll position, the reader's render
    /// bridge); it survives activity recreation with the entry.
    /// </summary>
    public Dictionary<string, object> State { get; } = new(StringComparer.Ordinal);
    public CoreBaseViewModel? ViewModel { get; }
    public IDispatcher Dispatcher { get; }

    /// <summary>The fragment currently showing this entry, if any.</summary>
    public WinoFragment? Fragment { get; internal set; }

    /// <summary>True while the ViewModel is still running its OnNavigatedTo work.</summary>
    public bool HasPendingWork => !_activation.IsCompleted;

    public bool IsReleased => _released;

    /// <summary>A new view for this entry. Main thread.</summary>
    public WinoFragment CreateFragment()
    {
        var fragment = _createFragment();
        fragment.Entry = this;
        return fragment;
    }

    /// <summary>Runs OnNavigatedTo once, then OnPageLoaded, on the UI thread (the WinoViewController order).</summary>
    public async Task ActivateAsync(NavigationMode mode)
    {
        if (_activated || _released || ViewModel is not { } viewModel) return;
        _activated = true;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            Preparing?.Invoke(this);
            _activation = InvokeNavigatedTo(viewModel, mode);
        });
        await _activation;
        await Dispatcher.ExecuteOnUIThread(() => { if (!_released) viewModel.OnPageLoaded(); });
    }

    private async Task InvokeNavigatedTo(CoreBaseViewModel viewModel, NavigationMode mode)
    {
        if (Initialize is { } initialize) await initialize(this, mode);
        else viewModel.OnNavigatedTo(mode, Parameter);
        if (Activated is { } activated) await activated(this);
    }


    /// <summary>Runs OnNavigatedFrom once; the entry is not shown again.</summary>
    public async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        try { await _activation; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Page {Page} failed while it was being shown.", Page); }
        if (_activated && ViewModel is { } viewModel)
        {
            if (Deactivate is { } deactivate)
            {
                try
                {
                    Task pending = Task.CompletedTask;
                    await Dispatcher.ExecuteOnUIThread(() => pending = deactivate(this));
                    await pending;
                }
                catch (Exception exception) { Serilog.Log.Warning(exception, "Page {Page} failed while it was being released.", Page); }
            }
            else await Dispatcher.ExecuteOnUIThread(() => viewModel.OnNavigatedFrom(NavigationMode.New, null));
        }
        // Page state that outlives views (controllers, bridges) goes with the page.
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var disposable in State.Values.OfType<IDisposable>()) disposable.Dispose();
            State.Clear();
        });
    }
}

/// <summary>
/// Base of every Android page. The view comes from an XML layout; <see cref="OnBind"/> connects it to the entry's
/// ViewModel through <see cref="Bindings"/>, which are disposed with the view. The navigation service owns activation
/// and release (<see cref="PageEntry"/>), so a fragment that is destroyed with its activity leaves the ViewModel alone.
/// </summary>
public abstract class WinoFragment : Fragment
{
    /// <summary>The entry this fragment shows. Set by <see cref="PageEntry.CreateFragment"/> before the fragment is added.</summary>
    public PageEntry? Entry { get; internal set; }

    /// <summary>Bindings between the current view and the ViewModel; replaced with every new view.</summary>
    protected BindingScope Bindings { get; private set; } = new();

    protected abstract int LayoutResource { get; }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
        => inflater.Inflate(LayoutResource, container, false);

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        Bindings.Dispose();
        Bindings = new BindingScope();
        // Without an entry (the system restored this fragment after the process ended) the view stays empty until
        // startup navigates.
        if (Entry is null) return;
        Entry.Fragment = this;
        OnBind(view);
    }

    public override void OnDestroyView()
    {
        Bindings.Dispose();
        if (Entry is { } entry && ReferenceEquals(entry.Fragment, this)) entry.Fragment = null;
        base.OnDestroyView();
    }

    /// <summary>Connects the inflated view to the entry's ViewModel. Main thread.</summary>
    protected abstract void OnBind(View view);

    protected void ReportError(Exception exception) => Serilog.Log.Error(exception, "Page {Page} failed.", GetType().Name);

    /// <summary>Runs <paramref name="command"/> when <paramref name="button"/> is clicked and follows its CanExecute.</summary>
    protected void BindCommand(View button, ICommand command, IDispatcher dispatcher, Func<object?>? parameter = null)
    {
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.Click += handler;
        Bindings.Own(new ActionDisposable(() => button.Click -= handler));
    }

    /// <summary>Runs <paramref name="action"/> when <paramref name="button"/> is clicked while the view exists.</summary>
    protected void OnClick(MaterialButton button, Action action)
    {
        EventHandler handler = (_, _) => action();
        button.Click += handler;
        Bindings.Own(new ActionDisposable(() => button.Click -= handler));
    }
}

/// <summary>A page with a ViewModel of type <typeparamref name="TViewModel"/>.</summary>
public abstract class WinoFragment<TViewModel> : WinoFragment where TViewModel : CoreBaseViewModel
{
    /// <summary>The entry's ViewModel; null only for a fragment the system restored without an entry.</summary>
    protected TViewModel? ViewModel => Entry?.ViewModel as TViewModel;

    protected sealed override void OnBind(View view)
    {
        if (ViewModel is { } viewModel && Entry is { } entry) OnBind(view, viewModel, entry.Dispatcher);
    }

    protected abstract void OnBind(View view, TViewModel viewModel, IDispatcher dispatcher);

    /// <summary>Applies the ViewModel property now and whenever it changes.</summary>
    protected void Bind<TValue>(TViewModel viewModel, IDispatcher dispatcher, string property, Func<TViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<TViewModel, TValue>(viewModel, property, read, apply, dispatcher, ReportError));
}

/// <summary>Runs an action once when disposed; detaches event handlers inside a <see cref="BindingScope"/>.</summary>
public sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
