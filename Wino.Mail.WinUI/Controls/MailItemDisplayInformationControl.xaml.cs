using System;
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

namespace Wino.Controls;

public sealed partial class MailItemDisplayInformationControl : UserControl
{
    public ImagePreviewControl GetImagePreviewControl() => ContactImage;

    public bool IsRunningHoverAction { get; set; }

    // Busy animation fields
    private Compositor? _compositor;
    private Visual? _contentVisual;
    private ScalarKeyFrameAnimation? _opacityAnimation;

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

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool Prefer24HourTimeFormat { get; set; }

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
        Prefer24HourTimeFormat = preferencesService.Prefer24HourTimeFormat;
        LeftHoverAction = preferencesService.LeftHoverAction;
        CenterHoverAction = preferencesService.CenterHoverAction;
        RightHoverAction = preferencesService.RightHoverAction;

        var compositor = this.Visual().Compositor;

        var leftBackgroundVisual = compositor.CreateSpriteVisual();
        RootContainerVisualWrapper.SetChildVisual(leftBackgroundVisual);
        MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        IconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainerVisualWrapper.SizeChanged += (s, e) => leftBackgroundVisual.Size = e.NewSize.ToVector2();

        // Initialize shimmer effect compositor
        _compositor = this.Visual().Compositor;
    }

    partial void OnMailItemInformationPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (ActionItem == null && MailItemInformation is IMailListItem mailListItem)
        {
            ActionItem = mailListItem;
        }

        UpdateBusyAnimationState();
    }

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
        if (MailItemInformation?.IsBusy == true)
        {
            StartBusyAnimation();
            return;
        }

        StopBusyAnimation();
    }

    private void ControlPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsHoverActionsEnabled)
        {
            HoverActionButtons.Visibility = Visibility.Visible;
            UnreadContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void ControlPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsHoverActionsEnabled)
        {
            HoverActionButtons.Visibility = Visibility.Collapsed;
            UnreadContainer.Visibility = Visibility.Visible;
        }
    }

    private void ExecuteHoverAction(MailOperation operation)
    {
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
}
