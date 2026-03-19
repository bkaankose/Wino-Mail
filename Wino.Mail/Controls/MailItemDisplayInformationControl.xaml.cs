using System.Linq;
using System.Numerics;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.UWP.Controls;
using Wino.Extensions;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls;

public sealed partial class MailItemDisplayInformationControl : UserControl
{
    public ImagePreviewControl GetImagePreviewControl() => ContactImage;

    public bool IsRunningHoverAction { get; set; }

    private bool _isPointerOver;

    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(nameof(DisplayMode), typeof(MailListDisplayMode), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailListDisplayMode.Spacious, OnLayoutPropertyChanged));
    public static readonly DependencyProperty ShowPreviewTextProperty = DependencyProperty.Register(nameof(ShowPreviewText), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true, OnLayoutPropertyChanged));
    public static readonly DependencyProperty IsCustomFocusedProperty = DependencyProperty.Register(nameof(IsCustomFocused), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsAvatarVisibleProperty = DependencyProperty.Register(nameof(IsAvatarVisible), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
    public static readonly DependencyProperty IsSubjectVisibleProperty = DependencyProperty.Register(nameof(IsSubjectVisible), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
    public static readonly DependencyProperty ConnectedExpanderProperty = DependencyProperty.Register(nameof(ConnectedExpander), typeof(WinoExpander), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null));
    public static readonly DependencyProperty LeftHoverActionProperty = DependencyProperty.Register(nameof(LeftHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None, OnHoverActionAppearanceChanged));
    public static readonly DependencyProperty CenterHoverActionProperty = DependencyProperty.Register(nameof(CenterHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None, OnHoverActionAppearanceChanged));
    public static readonly DependencyProperty RightHoverActionProperty = DependencyProperty.Register(nameof(RightHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None, OnHoverActionAppearanceChanged));
    public static readonly DependencyProperty HoverActionSizeProperty = DependencyProperty.Register(nameof(HoverActionSize), typeof(HoverActionSize), typeof(MailItemDisplayInformationControl), new PropertyMetadata(Core.Domain.Enums.HoverActionSize.Standard, OnHoverActionAppearanceChanged));
    public static readonly DependencyProperty HoverActionExecutedCommandProperty = DependencyProperty.Register(nameof(HoverActionExecutedCommand), typeof(ICommand), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null));
    public static readonly DependencyProperty MailItemProperty = DependencyProperty.Register(nameof(MailItem), typeof(IMailItem), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null, OnMailItemChanged));
    public static readonly DependencyProperty IsHoverActionsEnabledProperty = DependencyProperty.Register(nameof(IsHoverActionsEnabled), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true, OnHoverActionAppearanceChanged));
    public static readonly DependencyProperty Prefer24HourTimeFormatProperty = DependencyProperty.Register(nameof(Prefer24HourTimeFormat), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsThreadExpanderVisibleProperty = DependencyProperty.Register(nameof(IsThreadExpanderVisible), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsThreadExpandedProperty = DependencyProperty.Register(nameof(IsThreadExpanded), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsThumbnailUpdatedProperty = DependencyProperty.Register(nameof(IsThumbnailUpdated), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));

    public bool IsThumbnailUpdated
    {
        get { return (bool)GetValue(IsThumbnailUpdatedProperty); }
        set { SetValue(IsThumbnailUpdatedProperty, value); }
    }

    public bool IsThreadExpanded
    {
        get { return (bool)GetValue(IsThreadExpandedProperty); }
        set { SetValue(IsThreadExpandedProperty, value); }
    }

    public bool IsThreadExpanderVisible
    {
        get { return (bool)GetValue(IsThreadExpanderVisibleProperty); }
        set { SetValue(IsThreadExpanderVisibleProperty, value); }
    }

    public bool Prefer24HourTimeFormat
    {
        get { return (bool)GetValue(Prefer24HourTimeFormatProperty); }
        set { SetValue(Prefer24HourTimeFormatProperty, value); }
    }

    public bool IsHoverActionsEnabled
    {
        get { return (bool)GetValue(IsHoverActionsEnabledProperty); }
        set { SetValue(IsHoverActionsEnabledProperty, value); }
    }

    public IMailItem MailItem
    {
        get { return (IMailItem)GetValue(MailItemProperty); }
        set { SetValue(MailItemProperty, value); }
    }

    public ICommand HoverActionExecutedCommand
    {
        get { return (ICommand)GetValue(HoverActionExecutedCommandProperty); }
        set { SetValue(HoverActionExecutedCommandProperty, value); }
    }

    public MailOperation LeftHoverAction
    {
        get { return (MailOperation)GetValue(LeftHoverActionProperty); }
        set { SetValue(LeftHoverActionProperty, value); }
    }

    public MailOperation CenterHoverAction
    {
        get { return (MailOperation)GetValue(CenterHoverActionProperty); }
        set { SetValue(CenterHoverActionProperty, value); }
    }

    public MailOperation RightHoverAction
    {
        get { return (MailOperation)GetValue(RightHoverActionProperty); }
        set { SetValue(RightHoverActionProperty, value); }
    }

    public HoverActionSize HoverActionSize
    {
        get { return (HoverActionSize)GetValue(HoverActionSizeProperty); }
        set { SetValue(HoverActionSizeProperty, value); }
    }

    public WinoExpander ConnectedExpander
    {
        get { return (WinoExpander)GetValue(ConnectedExpanderProperty); }
        set { SetValue(ConnectedExpanderProperty, value); }
    }

    public bool IsSubjectVisible
    {
        get { return (bool)GetValue(IsSubjectVisibleProperty); }
        set { SetValue(IsSubjectVisibleProperty, value); }
    }

    public bool IsAvatarVisible
    {
        get { return (bool)GetValue(IsAvatarVisibleProperty); }
        set { SetValue(IsAvatarVisibleProperty, value); }
    }

    public bool IsCustomFocused
    {
        get { return (bool)GetValue(IsCustomFocusedProperty); }
        set { SetValue(IsCustomFocusedProperty, value); }
    }

    public bool ShowPreviewText
    {
        get { return (bool)GetValue(ShowPreviewTextProperty); }
        set { SetValue(ShowPreviewTextProperty, value); }
    }

    public MailListDisplayMode DisplayMode
    {
        get { return (MailListDisplayMode)GetValue(DisplayModeProperty); }
        set { SetValue(DisplayModeProperty, value); }
    }

    public MailItemDisplayInformationControl()
    {
        this.InitializeComponent();

        var compositor = this.Visual().Compositor;

        var leftBackgroundVisual = compositor.CreateSpriteVisual();
        RootContainerVisualWrapper.SetChildVisual(leftBackgroundVisual);
        MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ModernTopRightArea.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        CompactIconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainerVisualWrapper.SizeChanged += (s, e) => leftBackgroundVisual.Size = e.NewSize.ToVector2();
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private static void OnMailItemChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MailItemDisplayInformationControl control)
        {
            control.UpdateInformation();
            control.UpdateLayoutForDisplayModeAndPreview();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MailItemDisplayInformationControl control)
        {
            control.UpdateLayoutForDisplayModeAndPreview();
            control.UpdateHoverState(control._isPointerOver);
        }
    }

    private static void OnHoverActionAppearanceChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MailItemDisplayInformationControl control)
        {
            control.UpdateHoverActionAppearance();
            control.UpdateHoverState(control._isPointerOver);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateInformation();
        UpdateLayoutForDisplayModeAndPreview();
        UpdateHoverActionAppearance();
        UpdateHoverState(false);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateHoverActionAppearance();
    }

    private void UpdateInformation()
    {
        if (MailItem == null) return;

        TitleText.Text = string.IsNullOrWhiteSpace(MailItem.Subject) ? Translator.MailItemNoSubject : MailItem.Subject;
    }

    private void UpdateLayoutForDisplayModeAndPreview()
    {
        bool isCompact = DisplayMode == MailListDisplayMode.Compact;
        bool showPreview = ShowPreviewText && !isCompact;

        CompactTopRightArea.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        CompactIconsContainer.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;

        ModernTopRightArea.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        PreviewTextContainerRoot.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewTimestampText.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        SubjectTimestampText.Visibility = isCompact || !ShowPreviewText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHoverActionAppearance()
    {
        UpdateHoverActionButton(ModernFirstActionButton, ModernFirstActionIcon, LeftHoverAction);
        UpdateHoverActionButton(ModernSecondActionButton, ModernSecondActionIcon, CenterHoverAction);
        UpdateHoverActionButton(ModernThirdActionButton, ModernThirdActionIcon, RightHoverAction);

        bool isLarge = HoverActionSize == Core.Domain.Enums.HoverActionSize.Large;
        double buttonWidth = isLarge ? 46 : 36;
        double buttonHeight = isLarge ? 52 : 42;
        double iconSize = isLarge ? 20 : 16;
        double spacing = isLarge ? 10 : 6;
        Thickness padding = isLarge ? new Thickness(11, 9, 11, 9) : new Thickness(8, 6, 8, 6);

        ModernHoverActionsContainer.Spacing = spacing;

        ApplyHoverActionSizing(ModernFirstActionButton, ModernFirstActionIcon, buttonWidth, buttonHeight, iconSize, padding);
        ApplyHoverActionSizing(ModernSecondActionButton, ModernSecondActionIcon, buttonWidth, buttonHeight, iconSize, padding);
        ApplyHoverActionSizing(ModernThirdActionButton, ModernThirdActionIcon, buttonWidth, buttonHeight, iconSize, padding);
    }

    private void ApplyHoverActionSizing(Button button, WinoFontIcon icon, double width, double height, double iconSize, Thickness padding)
    {
        button.Width = width;
        button.Height = height;
        button.MinWidth = width;
        button.MinHeight = height;
        button.Padding = padding;
        icon.FontSize = iconSize;
    }

    private void UpdateHoverActionButton(Button button, WinoFontIcon icon, MailOperation operation)
    {
        icon.Foreground = GetOperationBrush(operation);
        ToolTipService.SetToolTip(button, XamlHelpers.GetOperationString(operation));
    }

    private Brush GetOperationBrush(MailOperation operation)
    {
        return operation switch
        {
            MailOperation.SoftDelete or MailOperation.HardDelete => GetBrushResource("DeleteBrush"),
            MailOperation.SetFlag or MailOperation.ClearFlag => GetBrushResource("FlaggedBrush"),
            MailOperation.Archive or MailOperation.UnArchive or MailOperation.MarkAsRead or MailOperation.MarkAsUnread => GetAccentBrush(),
            MailOperation.MoveToJunk or MailOperation.MarkAsNotJunk => GetBrushResource("InformationBrush"),
            _ => GetBrushResource("InformationBrush")
        };
    }

    private Brush GetBrushResource(string key)
    {
        if (Application.Current.Resources.ContainsKey(key) && Application.Current.Resources[key] is Brush brush)
            return brush;

        if (Application.Current.Resources.ContainsKey("InformationBrush") && Application.Current.Resources["InformationBrush"] is Brush informationBrush)
            return informationBrush;

        return new SolidColorBrush(Colors.Black);
    }

    private Brush GetAccentBrush()
    {
        if (Application.Current.Resources.ContainsKey("SystemAccentColor") && Application.Current.Resources["SystemAccentColor"] is Color accentColor)
            return new SolidColorBrush(accentColor);

        return GetBrushResource("InformationBrush");
    }

    private void ControlPointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateHoverState(true);
    }

    private void ControlPointerExited(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateHoverState(false);
    }

    private void UpdateHoverState(bool isPointerOver)
    {
        bool isCompact = DisplayMode == MailListDisplayMode.Compact;
        bool shouldShowHoverActions = IsHoverActionsEnabled && isPointerOver;

        if (isCompact)
        {
            CompactHoverActionButtons.Visibility = shouldShowHoverActions ? Visibility.Visible : Visibility.Collapsed;
            CompactUnreadContainer.Visibility = shouldShowHoverActions ? Visibility.Collapsed : Visibility.Visible;

            ModernHoverActionsContainer.Visibility = Visibility.Collapsed;
            ModernNonHoverStatusContainer.Visibility = Visibility.Visible;
        }
        else
        {
            CompactHoverActionButtons.Visibility = Visibility.Collapsed;
            CompactUnreadContainer.Visibility = Visibility.Visible;

            ModernHoverActionsContainer.Visibility = shouldShowHoverActions ? Visibility.Visible : Visibility.Collapsed;
            ModernNonHoverStatusContainer.Visibility = shouldShowHoverActions ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ExecuteHoverAction(MailOperation operation)
    {
        IsRunningHoverAction = true;

        MailOperationPreperationRequest package = null;

        if (MailItem is MailCopy mailCopy)
            package = new MailOperationPreperationRequest(operation, mailCopy, toggleExecution: true);
        else if (MailItem is ThreadMailItemViewModel threadMailItemViewModel)
            package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.GetMailCopies(), toggleExecution: true);
        else if (MailItem is ThreadMailItem threadMailItem)
            package = new MailOperationPreperationRequest(operation, threadMailItem.ThreadItems.Cast<MailItemViewModel>().Select(a => a.MailCopy), toggleExecution: true);

        if (package == null) return;

        HoverActionExecutedCommand?.Execute(package);
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
