using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.Controls.Playground.Lifetime;
using Wino.Mail.Controls.Playground.Pages;

namespace Wino.Mail.Controls.Playground;

public sealed partial class MainPage : Page
{
    private static readonly PlaygroundRoute[] Routes =
    [
        new("accountIcon", typeof(AccountIconPage)),
        new("contact", typeof(ContactPicturePage)),
        new("mailList", typeof(MailListPage)),
        new("mailListInteractions", typeof(MailListInteractionsPage)),
        new("editor", typeof(EditorPage)),
        new("searchBar", typeof(SearchBarPage)),
        new("intelligenceHeader", typeof(IntelligenceHeaderPage)),
        new("intelligenceProgress", typeof(IntelligenceProgressPage)),
        new("synchronizationButton", typeof(SynchronizationButtonPage)),
        new("hoverActions", typeof(HoverActionsPage)),
        new("contextFlyout", typeof(ContextFlyoutPage)),
        new("hotKeyInput", typeof(HotKeyInputPage)),
        new("shimmer", typeof(ShimmerPage)),
    ];

    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly ControlLifetimeCoordinator _lifetimeCoordinator;
    private CancellationTokenSource? _sweepCancellation;
    private bool _suppressSelectionChanged;
    private bool _sweepRunning;

    public ObservableCollection<string> LifetimeTrace { get; } = [];

    public MainPage()
    {
        InitializeComponent();
        _lifetimeCoordinator = new(AddTrace);
        Navigation.SelectedItem = Navigation.MenuItems[0];
        UpdateFrameState();
    }

    private async void NavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged || _sweepRunning)
        {
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var route = Routes.FirstOrDefault(candidate => candidate.Tag == tag) ?? Routes[0];
        await NavigateAndVerifyAsync(route, prepareForSweep: false, CancellationToken.None);
    }

    private async Task<bool> NavigateAndVerifyAsync(
        PlaygroundRoute route,
        bool prepareForSweep,
        CancellationToken cancellationToken)
    {
        await _navigationGate.WaitAsync(cancellationToken);

        try
        {
            var snapshot = await _lifetimeCoordinator.ReplacePageAsync(ContentFrame, route.PageType);
            UpdateFrameState();

            if (ContentFrame.Content is not Page incomingPage)
            {
                ShowFailure($"Navigation to {route.PageType.Name} did not create a page.");
                return false;
            }

            await WaitUntilLoadedAsync(incomingPage, cancellationToken);
            if (prepareForSweep && incomingPage is IPlaygroundLifetimeAware lifetimeAware)
            {
                await lifetimeAware.PrepareForLifetimeTestAsync(cancellationToken);
            }

            _lifetimeCoordinator.TraceLoaded(incomingPage);
            if (snapshot is null)
            {
                return true;
            }

            ShowPending($"Checking {snapshot.PageName} and {snapshot.ControlCount} control instance(s)…");
            var result = await _lifetimeCoordinator.VerifyReleasedAsync(snapshot, cancellationToken);
            if (result.Passed)
            {
                ShowSuccess($"Released {result.PageName} and all {result.ControlCount} control instance(s). Back and forward stacks are empty.");
                return true;
            }

            ShowFailure($"{result.PageName} retained {result.RetainedObjects.Count} object(s): {string.Join(", ", result.RetainedObjects)}");
            return false;
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private async void RunMemorySweepClicked(object sender, RoutedEventArgs args)
    {
        if (_sweepRunning)
        {
            return;
        }

        _sweepRunning = true;
        _sweepCancellation = new CancellationTokenSource();
        Navigation.IsPaneOpen = false;
        Navigation.IsPaneToggleButtonVisible = false;
        RunMemorySweepButton.IsEnabled = false;
        CancelMemorySweepButton.IsEnabled = true;
        ShowPending("Running three memory-sweep cycles across all playground pages…");

        try
        {
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                foreach (var route in Routes)
                {
                    _sweepCancellation.Token.ThrowIfCancellationRequested();
                    SelectRoute(route);
                    AddTrace($"Sweep {cycle}/3: {route.PageType.Name}");
                    if (!await NavigateAndVerifyAsync(route, prepareForSweep: true, _sweepCancellation.Token))
                    {
                        ShowFailure($"Memory sweep stopped during cycle {cycle} on {route.PageType.Name}.");
                        return;
                    }
                }
            }

            var finalRoute = Routes[0];
            SelectRoute(finalRoute);
            if (!await NavigateAndVerifyAsync(finalRoute, prepareForSweep: true, _sweepCancellation.Token))
            {
                ShowFailure("Memory sweep failed while releasing the final page.");
                return;
            }

            ShowSuccess("Memory sweep passed: three cycles, thirteen routes, and no retained pages or controls.");
        }
        catch (OperationCanceledException)
        {
            LifetimeStatusInfoBar.Severity = InfoBarSeverity.Warning;
            LifetimeStatusText.Text = "Memory sweep cancelled.";
            AddTrace("Memory sweep cancelled.");
        }
        catch (Exception exception)
        {
            ShowFailure($"Memory sweep failed: {exception.Message}");
        }
        finally
        {
            _sweepCancellation.Dispose();
            _sweepCancellation = null;
            _sweepRunning = false;
            Navigation.IsPaneToggleButtonVisible = true;
            RunMemorySweepButton.IsEnabled = true;
            CancelMemorySweepButton.IsEnabled = false;
            UpdateFrameState();
        }
    }

    private void CancelMemorySweepClicked(object sender, RoutedEventArgs args) => _sweepCancellation?.Cancel();

    private void SelectRoute(PlaygroundRoute route)
    {
        _suppressSelectionChanged = true;
        try
        {
            Navigation.SelectedItem = Navigation.MenuItems
                .OfType<NavigationViewItem>()
                .First(item => string.Equals(item.Tag as string, route.Tag, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private static async Task WaitUntilLoadedAsync(Page page, CancellationToken cancellationToken)
    {
        if (!page.IsLoaded)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void Loaded(object sender, RoutedEventArgs args) => completion.TrySetResult();

            page.Loaded += Loaded;
            try
            {
                if (!page.IsLoaded)
                {
                    await completion.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                page.Loaded -= Loaded;
            }
        }

        await Task.Yield();
    }

    private void ShowPending(string message)
    {
        LifetimeStatusInfoBar.Severity = InfoBarSeverity.Informational;
        LifetimeStatusText.Text = message;
        AddTrace(message);
    }

    private void ShowSuccess(string message)
    {
        LifetimeStatusInfoBar.Severity = InfoBarSeverity.Success;
        LifetimeStatusText.Text = message;
        AddTrace(message);
    }

    private void ShowFailure(string message)
    {
        LifetimeStatusInfoBar.Severity = InfoBarSeverity.Error;
        LifetimeStatusText.Text = message;
        AddTrace(message);
    }

    private void UpdateFrameState()
    {
        FrameStateText.Text = $"Cache: {ContentFrame.CacheSize} · Back: {ContentFrame.BackStack.Count} · Forward: {ContentFrame.ForwardStack.Count}";
    }

    private void AddTrace(string message)
    {
        LifetimeTrace.Insert(0, $"{DateTime.Now:T}  {message}");
        while (LifetimeTrace.Count > 100)
        {
            LifetimeTrace.RemoveAt(LifetimeTrace.Count - 1);
        }
    }

    private sealed record PlaygroundRoute(string Tag, Type PageType);
}
