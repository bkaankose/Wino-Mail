using System;
using System.Numerics;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
using MailCal.Calendar.Controls;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Controls;

public sealed partial class CalendarTitleBarContent : UserControl
{
    /// <summary>
    /// The month rail runs vertically, so its arrows point up and down: a quarter turn clockwise
    /// takes the left arrow to up and the right arrow to down.
    /// </summary>
    private const float MonthArrowRotationDegrees = 90f;
    private static readonly TimeSpan ArrowRotationDuration = TimeSpan.FromMilliseconds(260);

    private readonly UISettings _uiSettings = new();
    private bool _hasPresentedArrows;

    public event EventHandler? PreviousDateRequested;
    public event EventHandler? NextDateRequested;

    public CalendarTitleBarContent()
    {
        InitializeComponent();

        // A dependency property callback has no XAML form; the arrows follow the selected type.
        CalendarTypeSelector.RegisterPropertyChangedCallback(MailCalCalendarTypeSelectorControl.SelectedTypeProperty, SelectedTypeChangedForArrows);
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        // The first presentation takes the resting pose without a turn.
        ApplyArrowRotation(animate: false);
        _hasPresentedArrows = true;
    }

    private void SelectedTypeChangedForArrows(DependencyObject sender, DependencyProperty dp)
        => ApplyArrowRotation(animate: _hasPresentedArrows && _uiSettings.AnimationsEnabled);

    private void ApplyArrowRotation(bool animate)
    {
        var angle = SelectedType == CalendarDisplayType.Month ? MonthArrowRotationDegrees : 0f;

        RotateArrow(PreviousArrow, angle, animate);
        RotateArrow(NextArrow, angle, animate);
    }

    private static void RotateArrow(FrameworkElement arrow, float angle, bool animate)
    {
        var visual = ElementCompositionPreview.GetElementVisual(arrow);

        visual.CenterPoint = new Vector3((float)(arrow.ActualWidth * 0.5), (float)(arrow.ActualHeight * 0.5), 0f);
        visual.StopAnimation(nameof(visual.RotationAngleInDegrees));

        if (!animate)
        {
            visual.RotationAngleInDegrees = angle;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var rotation = compositor.CreateScalarKeyFrameAnimation();

        rotation.InsertKeyFrame(1f, angle, easing);
        rotation.Duration = ArrowRotationDuration;

        visual.StartAnimation(nameof(visual.RotationAngleInDegrees), rotation);
    }

    public string VisibleDateRangeText
    {
        get => VisibleDateRangeTextBlock.Text;
        set => VisibleDateRangeTextBlock.Text = value;
    }

    public ICommand? TodayClickedCommand
    {
        get => CalendarTypeSelector.TodayClickedCommand;
        set => CalendarTypeSelector.TodayClickedCommand = value;
    }

    public int DisplayDayCount
    {
        get => CalendarTypeSelector.DisplayDayCount;
        set => CalendarTypeSelector.DisplayDayCount = value;
    }

    public CalendarDisplayType SelectedType
    {
        get => CalendarTypeSelector.SelectedType;
        set => CalendarTypeSelector.SelectedType = value;
    }

    public long RegisterSelectedTypeChanged(DependencyPropertyChangedCallback callback)
        => CalendarTypeSelector.RegisterPropertyChangedCallback(MailCalCalendarTypeSelectorControl.SelectedTypeProperty, callback);

    public void UnregisterSelectedTypeChanged(long token)
        => CalendarTypeSelector.UnregisterPropertyChangedCallback(MailCalCalendarTypeSelectorControl.SelectedTypeProperty, token);

    private void PreviousDateClicked(object sender, RoutedEventArgs e)
        => PreviousDateRequested?.Invoke(this, EventArgs.Empty);

    private void NextDateClicked(object sender, RoutedEventArgs e)
        => NextDateRequested?.Invoke(this, EventArgs.Empty);
}
