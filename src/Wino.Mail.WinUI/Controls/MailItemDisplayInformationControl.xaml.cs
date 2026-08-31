using System;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Helpers;

namespace Wino.Controls;

public sealed partial class MailItemDisplayInformationControl : UserControl
{
    public bool IsRunningHoverAction { get; set; }

    // Busy animation fields
    private Compositor? _compositor;
    private Visual? _contentVisual;
    private ScalarKeyFrameAnimation? _opacityAnimation;
    private SpriteVisual? _leftBackgroundVisual;
    private INotifyPropertyChanged? _actionItemPropertySource;
    private FeatheredHoverActionAnimator? _hoverActionAnimator;

    [GeneratedDependencyProperty(DefaultValue = MailListDisplayMode.Spacious)]
    public partial MailListDisplayMode DisplayMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool ShowPreviewText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsAvatarVisible { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSubjectVisible { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation LeftHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation CenterHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation RightHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsHoverActionsEnabled { get; set; }

    public event EventHandler<MailOperationPreperationRequest>? HoverActionExecuted;

    [GeneratedDependencyProperty(DefaultValue = Wino.Core.Domain.Enums.TimeFormatPreference.UseLanguageCulture)]
    public partial TimeFormatPreference TimeFormatPreference { get; set; }

    [GeneratedDependencyProperty]
    public partial IMailListItem? ActionItem { get; set; }

    [GeneratedDependencyProperty]
    public partial IMailItemDisplayInformation? MailItemInformation { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsThreadExpanderVisible { get; set; }

    public MailItemDisplayInformationControl()
    {
        InitializeComponent();

        // Initialize properties from IPreferencesService for AOT compatibility
        var preferencesService = App.Current.Services.GetRequiredService<IPreferencesService>();

        DisplayMode = preferencesService.MailItemDisplayMode;
        ShowPreviewText = preferencesService.IsShowPreviewEnabled;
        IsAvatarVisible = preferencesService.IsShowSenderPicturesEnabled;
        IsHoverActionsEnabled = preferencesService.IsHoverActionsEnabled;
        TimeFormatPreference = preferencesService.MailTimeFormatPreference;
        LeftHoverAction = preferencesService.LeftHoverAction;
        CenterHoverAction = preferencesService.CenterHoverAction;
        RightHoverAction = preferencesService.RightHoverAction;

        _compositor = this.Visual().Compositor;
        InitializeLeftBackgroundVisual();
        MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        IconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

    }

    partial void OnMailItemInformationPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (ActionItem == null && MailItemInformation is IMailListItem mailListItem)
        {
            ActionItem = mailListItem;
        }

        UpdateBusyAnimationState();
    }

    partial void OnActionItemPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_actionItemPropertySource != null)
        {
            _actionItemPropertySource.PropertyChanged -= ActionItemPropertyChanged;
            _actionItemPropertySource = null;
        }

        if (e.NewValue is INotifyPropertyChanged propertyChangedSource)
        {
            _actionItemPropertySource = propertyChangedSource;
            _actionItemPropertySource.PropertyChanged += ActionItemPropertyChanged;
        }

        UpdateBusyAnimationState();
    }

    partial void OnLeftHoverActionPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateHoverActionAvailability();

    partial void OnCenterHoverActionPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateHoverActionAvailability();

    partial void OnRightHoverActionPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateHoverActionAvailability();

    partial void OnIsHoverActionsEnabledPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateHoverActionAvailability();

    private void StartBusyAnimation()
    {
        if (_compositor == null) return;

        // Get the visual for the content area
        _contentVisual = ElementCompositionPreview.GetElementVisual(MainContentContainer);

        // Create a subtle opacity pulse animation (1.0 -> 0.4 -> 1.0)
        _opacityAnimation = _compositor.CreateScalarKeyFrameAnimation();
        _opacityAnimation.InsertKeyFrame(0f, 1f);
        _opacityAnimation.InsertKeyFrame(0.5f, 0.4f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0f), new Vector2(0.58f, 1f)));
        _opacityAnimation.InsertKeyFrame(1f, 1f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0f), new Vector2(0.58f, 1f)));
        _opacityAnimation.Duration = TimeSpan.FromSeconds(1.0);
        _opacityAnimation.IterationBehavior = AnimationIterationBehavior.Forever;

        // Start animation
        _contentVisual.StartAnimation("Opacity", _opacityAnimation);
    }

    private void StopBusyAnimation()
    {
        if (_contentVisual != null)
        {
            _contentVisual.StopAnimation("Opacity");

            // Reset to default value
            _contentVisual.Opacity = 1f;

            _contentVisual = null;
        }

        _opacityAnimation = null;
    }

    private void UpdateBusyAnimationState()
    {
        if (ActionItem?.IsBusy == true)
        {
            StartBusyAnimation();
            return;
        }

        StopBusyAnimation();
    }

    private void ActionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IMailListItem.IsBusy))
        {
            UpdateBusyAnimationState();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_actionItemPropertySource != null)
        {
            _actionItemPropertySource.PropertyChanged -= ActionItemPropertyChanged;
            _actionItemPropertySource = null;
        }

        StopBusyAnimation();
        _hoverActionAnimator?.Dispose();
        _hoverActionAnimator = null;
        HoverActionButtons.Visibility = Visibility.Collapsed;
        UnreadContainer.Visibility = Visibility.Visible;
        ElementCompositionPreview.SetElementChildVisual(RootContainerVisualWrapper, null);
        _leftBackgroundVisual?.Dispose();
        _leftBackgroundVisual = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_actionItemPropertySource == null && ActionItem is INotifyPropertyChanged propertyChangedSource)
        {
            _actionItemPropertySource = propertyChangedSource;
            _actionItemPropertySource.PropertyChanged += ActionItemPropertyChanged;
        }

        InitializeLeftBackgroundVisual();
        UpdateBusyAnimationState();
    }

    private void InitializeLeftBackgroundVisual()
    {
        if (_leftBackgroundVisual != null || _compositor == null)
            return;

        _leftBackgroundVisual = _compositor.CreateSpriteVisual();
        _leftBackgroundVisual.Size = RootContainerVisualWrapper.ActualSize;
        RootContainerVisualWrapper.SetChildVisual(_leftBackgroundVisual);
    }

    private void RootContainerVisualWrapperSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_leftBackgroundVisual != null)
        {
            _leftBackgroundVisual.Size = e.NewSize.ToVector2();
        }
    }

    private void ControlPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsHoverActionsEnabled && HasVisibleHoverActions())
        {
            _hoverActionAnimator ??= new FeatheredHoverActionAnimator(
                HoverActionButtons,
                HoverActionVeil,
                HoverActionButtonHost);
            _hoverActionAnimator.Show();
            UnreadContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void ControlPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_hoverActionAnimator != null)
        {
            _hoverActionAnimator.Hide(() => UnreadContainer.Visibility = Visibility.Visible);
        }
    }

    private void ExecuteHoverAction(MailOperation operation)
    {
        if (operation == MailOperation.None)
            return;

        IsRunningHoverAction = true;

        MailOperationPreperationRequest? package = null;

        if (ActionItem is MailItemViewModel mailItemViewModel)
            package = new MailOperationPreperationRequest(operation, mailItemViewModel.MailCopy, toggleExecution: true);

        else if (ActionItem is ThreadMailItemViewModel threadMailItemViewModel)
            package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.ThreadEmails.Select(a => a.MailCopy), toggleExecution: true);

        if (package == null) return;

        HoverActionExecuted?.Invoke(this, package);
    }

    private void FirstActionClicked(object sender, RoutedEventArgs e)
    {
        ExecuteHoverAction(LeftHoverAction);
    }

    private void SecondActionClicked(object sender, RoutedEventArgs e)
    {
        ExecuteHoverAction(CenterHoverAction);
    }

    private void ThirdActionClicked(object sender, RoutedEventArgs e)
    {
        ExecuteHoverAction(RightHoverAction);
    }

    private bool HasVisibleHoverActions()
        => LeftHoverAction != MailOperation.None ||
           CenterHoverAction != MailOperation.None ||
           RightHoverAction != MailOperation.None;

    private void UpdateHoverActionAvailability()
    {
        if (IsHoverActionsEnabled && HasVisibleHoverActions())
            return;

        _hoverActionAnimator?.Hide(() => UnreadContainer.Visibility = Visibility.Visible);
    }
}
