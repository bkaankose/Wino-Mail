using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Serilog;
using Windows.UI.ViewManagement;
using Wino.Mail.ViewModels;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// The local Daily Briefing surface. It overlays the shell without a scrim so the mail list remains
/// visible while the panel is open.
/// </summary>
public sealed partial class DailyBriefingPanel : UserControl
{
    private const double SlideDurationMilliseconds = 300;

    private readonly UISettings _uiSettings = new();
    private bool _isOpen;
    private CompositionScopedBatch? _closingBatch;

    public DailyBriefingPanel()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public DailyBriefingPanelViewModel ViewModel { get; } =
        WinoApplication.Current.Services.GetRequiredService<DailyBriefingPanelViewModel>();

    public bool IsOpen => _isOpen;

    public event EventHandler<bool>? IsOpenChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseRequested += ViewModelCloseRequested;
        ViewModel.PropertyChanged += ViewModelPropertyChanged;

        UpdateBriefingCollectionViewSource();
        ElementCompositionPreview.SetIsTranslationEnabled(PanelRoot, true);
        if (!_isOpen) SetTranslation(PanelWidth());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseRequested -= ViewModelCloseRequested;
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    public async Task OpenAsync()
    {
        if (_isOpen) return;
        _isOpen = true;
        _closingBatch = null;
        IsOpenChanged?.Invoke(this, true);

        Visibility = Visibility.Visible;
        UpdateLayout();
        AnimateTranslation(0);

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to initialize the local daily briefing panel.");
        }
    }

    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        IsOpenChanged?.Invoke(this, false);
        _ = ViewModel.MarkViewedAsync();

        var visual = GetVisual();
        if (!_uiSettings.AnimationsEnabled)
        {
            SetTranslation(PanelWidth());
            Visibility = Visibility.Collapsed;
            return;
        }

        var closingBatch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _closingBatch = closingBatch;
        closingBatch.Completed += (_, _) =>
        {
            if (ReferenceEquals(_closingBatch, closingBatch))
                _closingBatch = null;
            if (!_isOpen) Visibility = Visibility.Collapsed;
        };
        AnimateTranslation(PanelWidth());
        closingBatch.End();
    }

    public Task ToggleAsync() => _isOpen ? RunClose() : OpenAsync();

    private Task RunClose()
    {
        Close();
        return Task.CompletedTask;
    }

    private void ViewModelCloseRequested(object? sender, EventArgs e) => Close();
    private float PanelWidth() => (float)(ActualWidth > 0 ? ActualWidth : MaxWidth);
    private Visual GetVisual() => ElementCompositionPreview.GetElementVisual(PanelRoot);

    private void SetTranslation(float x)
    {
        var visual = GetVisual();
        visual.Properties.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", new Vector3(x, 0, 0));
    }

    private void AnimateTranslation(float targetX)
    {
        if (!_uiSettings.AnimationsEnabled)
        {
            SetTranslation(targetX);
            return;
        }

        var visual = GetVisual();
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(targetX, 0, 0),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(SlideDurationMilliseconds);
        visual.Properties.StartAnimation("Translation", animation);
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void ActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DailyBriefingItem item })
            ViewModel.ExecuteActionCommand.Execute(item);
    }

    private void OpenItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DailyBriefingItem item })
            ViewModel.OpenItemCommand.Execute(item);
    }

    private void IgnoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DailyBriefingItem item })
            ViewModel.IgnoreCommand.Execute(item);
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DailyBriefingItem item })
            ViewModel.DeleteCommand.Execute(item);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedDateGroups))
            UpdateBriefingCollectionViewSource();
    }

    private void UpdateBriefingCollectionViewSource()
    {
        BriefingCollectionViewSource.Source = ViewModel.SelectedDateGroups;
    }

    private void ToggleIgnoreInvoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.Tag is DailyBriefingItem item)
            ViewModel.IgnoreCommand.Execute(item);
    }
}
