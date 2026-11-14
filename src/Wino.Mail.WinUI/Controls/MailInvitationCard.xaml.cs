using System;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;
using Wino.Core.Domain;
using Wino.Mail.ViewModels;

namespace Wino.Controls;

public sealed partial class MailInvitationCard : UserControl
{
    private readonly UISettings _uiSettings = new();
    private bool _isExpanded;

    [GeneratedDependencyProperty]
    public partial MailInvitationCardViewModel? Invitation { get; set; }

    public MailInvitationCard()
    {
        InitializeComponent();
        UpdateToggleAutomationName();
    }

    public string GetHeaderDetailText(string? summary, string? status, bool canRespond)
        => canRespond || string.IsNullOrEmpty(status) ? summary ?? string.Empty : status;

    private void HeaderToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        BodyRoot.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        RotateChevron();
        UpdateToggleAutomationName();
    }

    private void RotateChevron()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ChevronIcon);
        visual.CenterPoint = new Vector3((float)(ChevronIcon.ActualWidth / 2), (float)(ChevronIcon.ActualHeight / 2), 0);
        var angle = _isExpanded ? 180f : 0f;
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = angle;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, angle);
        animation.Duration = TimeSpan.FromMilliseconds(167);
        visual.StartAnimation("RotationAngleInDegrees", animation);
    }

    private void UpdateToggleAutomationName()
        => AutomationProperties.SetName(HeaderToggleButton,
            $"{Translator.MailInvitation_CardAutomationName}, {(_isExpanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed)}");

    private void HeaderRow_PointerEntered(object sender, PointerRoutedEventArgs e) => HeaderHoverBackground.Opacity = 1;

    private void HeaderRow_PointerExited(object sender, PointerRoutedEventArgs e) => HeaderHoverBackground.Opacity = 0;
}
