using System;
using System.ComponentModel;
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
    // Busy animation fields
    private Compositor? _compositor;
    private Visual? _contentVisual;
    private ScalarKeyFrameAnimation? _opacityAnimation;
    private SpriteVisual? _leftBackgroundVisual;
    private INotifyPropertyChanged? _actionItemPropertySource;

    [GeneratedDependencyProperty(DefaultValue = MailListDisplayMode.Spacious)]
    public partial MailListDisplayMode DisplayMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool ShowPreviewText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsAvatarVisible { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSubjectVisible { get; set; }

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
        TimeFormatPreference = preferencesService.MailTimeFormatPreference;

        var compositor = this.Visual().Compositor;

        _leftBackgroundVisual = compositor.CreateSpriteVisual();
        RootContainerVisualWrapper.SetChildVisual(_leftBackgroundVisual);
        MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        IconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

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
    }

    private void RootContainerVisualWrapperSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_leftBackgroundVisual != null)
        {
            _leftBackgroundVisual.Size = e.NewSize.ToVector2();
        }
    }

}
