using System;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls;

public sealed partial class MailItemDisplayInformationControl : UserControl
{
    public ImagePreviewControl GetImagePreviewControl() => ContactImage;

    public bool IsRunningHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailListDisplayMode.Spacious)]
    public partial MailListDisplayMode DisplayMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool ShowPreviewText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsAvatarVisible { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSubjectVisible { get; set; }

    #region Display Properties

    [GeneratedDependencyProperty]
    public partial string? Subject { get; set; }

    [GeneratedDependencyProperty]
    public partial string? FromName { get; set; }

    [GeneratedDependencyProperty]
    public partial string? FromAddress { get; set; }

    [GeneratedDependencyProperty]
    public partial string? PreviewText { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsRead { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsDraft { get; set; }

    [GeneratedDependencyProperty]
    public partial bool HasAttachments { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsFlagged { get; set; }

    [GeneratedDependencyProperty]
    public partial DateTime CreationDate { get; set; }

    [GeneratedDependencyProperty]
    public partial string? Base64ContactPicture { get; set; }

    #endregion

    [GeneratedDependencyProperty]
    public partial WinoExpander? ConnectedExpander { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation LeftHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation CenterHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailOperation.None)]
    public partial MailOperation RightHoverAction { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? HoverActionExecutedCommand { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsHoverActionsEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool Prefer24HourTimeFormat { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsThreadExpanderVisible { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsThreadExpanded { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsThumbnailUpdated { get; set; }

    [GeneratedDependencyProperty]
    public partial IMailListItem? ActionItem { get; set; }

    public MailItemDisplayInformationControl()
    {
        InitializeComponent();

        var compositor = this.Visual().Compositor;

        var leftBackgroundVisual = compositor.CreateSpriteVisual();
        RootContainerVisualWrapper.SetChildVisual(leftBackgroundVisual);
        MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
        IconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

        RootContainerVisualWrapper.SizeChanged += (s, e) => leftBackgroundVisual.Size = e.NewSize.ToVector2();
    }

    partial void OnIsFlaggedChanged(bool newValue)
    {

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
