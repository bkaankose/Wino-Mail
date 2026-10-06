using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Playground.Lifetime;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class SynchronizationButtonPage : Page, IDisposable, IPlaygroundLifetimeAware
{
    private bool _isFakeSynchronizationRunning;
    private CancellationTokenSource? _synchronizationCancellation;
    private bool _disposed;

    public SynchronizationButtonPage()
    {
        InitializeComponent();
    }

    private void StateChanged(object sender, RoutedEventArgs e) => ApplyState();

    private void StateChanged(object sender, TextChangedEventArgs e) => ApplyState();

    private void ProgressChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyState();

    private void LiveButtonClicked(object sender, RoutedEventArgs e) => _ = RunFakeSynchronizationAsync();

    private void RunFakeSynchronizationClicked(object sender, RoutedEventArgs e) => _ = RunFakeSynchronizationAsync();

    private void ApplyState()
    {
        if (LiveButton is null || EnabledToggle is null || ProgressSlider is null || DescriptionBox is null || ToolTipBox is null)
        {
            return;
        }

        LiveButton.IsSynchronizing = SynchronizingToggle.IsOn;
        LiveButton.IsIndeterminate = IndeterminateToggle.IsOn;
        LiveButton.IsEnabled = EnabledToggle.IsOn;
        LiveButton.Progress = ProgressSlider.Value;
        LiveButton.Description = DescriptionBox.Text;
        LiveButton.IdleToolTip = ToolTipBox.Text;
    }

    /// <summary>
    /// Walks the ring from empty to full so the determinate state can be inspected without
    /// a real synchronizer behind it.
    /// </summary>
    private async Task RunFakeSynchronizationAsync()
    {
        if (_isFakeSynchronizationRunning)
        {
            return;
        }

        _isFakeSynchronizationRunning = true;
        var cancellationSource = new CancellationTokenSource();
        _synchronizationCancellation = cancellationSource;

        try
        {
            SynchronizingToggle.IsOn = true;
            IndeterminateToggle.IsOn = false;

            for (var value = 0d; value <= 100d; value += 4d)
            {
                ProgressSlider.Value = value;
                ApplyState();

                await Task.Delay(90, cancellationSource.Token);
            }

            SynchronizingToggle.IsOn = false;
            ApplyState();
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_synchronizationCancellation, cancellationSource))
            {
                _synchronizationCancellation = null;
            }

            cancellationSource.Dispose();
            _isFakeSynchronizationRunning = false;
        }
    }

    async Task IPlaygroundLifetimeAware.PrepareForLifetimeTestAsync(CancellationToken cancellationToken)
    {
        _ = RunFakeSynchronizationAsync();
        await Task.Delay(120, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _synchronizationCancellation?.Cancel();
        GC.SuppressFinalize(this);
    }
}
