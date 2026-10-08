using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Playground.Lifetime;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class IntelligenceProgressPage : Page, IDisposable, IPlaygroundLifetimeAware
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public IntelligenceProgressPage()
    {
        InitializeComponent();
    }

    private void AnimationsToggled(object sender, RoutedEventArgs e)
    {
        if (DotsProgress is null)
        {
            return;
        }

        SetAnimationState(AnimationsToggle.IsOn);
    }

    private void RestartAnimationsClicked(object sender, RoutedEventArgs e) => _ = RestartAnimationsAsync();

    private async Task RestartAnimationsAsync()
    {
        try
        {
            SetAnimationState(false);
            await Task.Delay(120, _lifetimeCancellation.Token);
            SetAnimationState(AnimationsToggle.IsOn);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetAnimationState(AnimationsToggle.IsOn);
        }
    }

    async Task IPlaygroundLifetimeAware.PrepareForLifetimeTestAsync(CancellationToken cancellationToken)
    {
        _ = RestartAnimationsAsync();
        await Task.Delay(30, cancellationToken);
    }

    private void SetAnimationState(bool isActive)
    {
        DotsProgress.IsActive = isActive;
        CubesProgress.IsActive = isActive;
        TranslateProgress.IsActive = isActive;
        SummarizeProgress.IsActive = isActive;
        RewriteProgress.IsActive = isActive;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
