using System;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Foundation;
using Wino.Calendar.Args;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public partial class WinoDayTimelineCanvas : Control, IDisposable
{
    public event EventHandler<TimelineCellSelectedArgs> TimelineCellSelected;
    public event EventHandler<TimelineCellUnselectedArgs> TimelineCellUnselected;

    private const string PART_InternalCanvas = nameof(PART_InternalCanvas);
    private SKXamlCanvas Canvas;

    public static readonly DependencyProperty RenderOptionsProperty = DependencyProperty.Register(nameof(RenderOptions), typeof(CalendarRenderOptions), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));
    public static readonly DependencyProperty SeperatorColorProperty = DependencyProperty.Register(nameof(SeperatorColor), typeof(SolidColorBrush), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));
    public static readonly DependencyProperty HalfHourSeperatorColorProperty = DependencyProperty.Register(nameof(HalfHourSeperatorColor), typeof(SolidColorBrush), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));
    public static readonly DependencyProperty SelectedCellBackgroundBrushProperty = DependencyProperty.Register(nameof(SelectedCellBackgroundBrush), typeof(SolidColorBrush), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));
    public static readonly DependencyProperty WorkingHourCellBackgroundColorProperty = DependencyProperty.Register(nameof(WorkingHourCellBackgroundColor), typeof(SolidColorBrush), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));
    public static readonly DependencyProperty SelectedDateTimeProperty = DependencyProperty.Register(nameof(SelectedDateTime), typeof(DateTime?), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null, new PropertyChangedCallback(OnSelectedDateTimeChanged)));
    public static readonly DependencyProperty PositionerUIElementProperty = DependencyProperty.Register(nameof(PositionerUIElement), typeof(UIElement), typeof(WinoDayTimelineCanvas), new PropertyMetadata(null));

    public UIElement PositionerUIElement
    {
        get { return (UIElement)GetValue(PositionerUIElementProperty); }
        set { SetValue(PositionerUIElementProperty, value); }
    }

    public CalendarRenderOptions RenderOptions
    {
        get { return (CalendarRenderOptions)GetValue(RenderOptionsProperty); }
        set { SetValue(RenderOptionsProperty, value); }
    }

    public SolidColorBrush HalfHourSeperatorColor
    {
        get { return (SolidColorBrush)GetValue(HalfHourSeperatorColorProperty); }
        set { SetValue(HalfHourSeperatorColorProperty, value); }
    }

    public SolidColorBrush SeperatorColor
    {
        get { return (SolidColorBrush)GetValue(SeperatorColorProperty); }
        set { SetValue(SeperatorColorProperty, value); }
    }

    public SolidColorBrush WorkingHourCellBackgroundColor
    {
        get { return (SolidColorBrush)GetValue(WorkingHourCellBackgroundColorProperty); }
        set { SetValue(WorkingHourCellBackgroundColorProperty, value); }
    }

    public SolidColorBrush SelectedCellBackgroundBrush
    {
        get { return (SolidColorBrush)GetValue(SelectedCellBackgroundBrushProperty); }
        set { SetValue(SelectedCellBackgroundBrushProperty, value); }
    }

    public DateTime? SelectedDateTime
    {
        get { return (DateTime?)GetValue(SelectedDateTimeProperty); }
        set { SetValue(SelectedDateTimeProperty, value); }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        Canvas = GetTemplateChild(PART_InternalCanvas) as SKXamlCanvas;

        if (Canvas != null)
        {
            Canvas.PaintSurface += OnCanvasPaintSurface;
            Canvas.PointerPressed += OnCanvasPointerPressed;
        }

        ForceDraw();
    }

    private static void OnSelectedDateTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WinoDayTimelineCanvas control)
        {
            if (e.OldValue != null && e.NewValue == null)
            {
                control.RaiseCellUnselected();
            }

            control.ForceDraw();
        }
    }

    private void RaiseCellUnselected()
    {
        TimelineCellUnselected?.Invoke(this, new TimelineCellUnselectedArgs());
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (RenderOptions == null) return;

        var hourHeight = RenderOptions.CalendarSettings.HourHeight;

        // When users click to cell we need to find the day, hour and minutes (first 30 minutes or second 30 minutes) that it represents on the timeline.

        if (PositionerUIElement == null)
        {
            PositionerUIElement = this.FindParents().LastOrDefault(a => a is Grid);
        }

        PointerPoint positionerRootPoint = e.GetCurrentPoint(PositionerUIElement);
        PointerPoint canvasPointerPoint = e.GetCurrentPoint(Canvas);

        Point touchPoint = canvasPointerPoint.Position;

        var singleDayWidth = (Canvas.ActualWidth / RenderOptions.TotalDayCount);

        int day = (int)(touchPoint.X / singleDayWidth);
        int hour = (int)(touchPoint.Y / hourHeight);

        bool isSecondHalf = touchPoint.Y % hourHeight > (hourHeight / 2);

        var diffX = positionerRootPoint.Position.X - touchPoint.X;
        var diffY = positionerRootPoint.Position.Y - touchPoint.Y;

        var cellStartRelativePositionX = diffX + (day * singleDayWidth);
        var cellEndRelativePositionX = cellStartRelativePositionX + singleDayWidth;

        var cellStartRelativePositionY = diffY + (hour * hourHeight) + (isSecondHalf ? hourHeight / 2 : 0);
        var cellEndRelativePositionY = cellStartRelativePositionY + (isSecondHalf ? (hourHeight / 2) : hourHeight);

        var cellSize = new Size(cellEndRelativePositionX - cellStartRelativePositionX, hourHeight / 2);
        var positionerPoint = new Point(cellStartRelativePositionX, cellStartRelativePositionY);

        var clickedDateTime = RenderOptions.DateRange.StartDate.AddDays(day).AddHours(hour).AddMinutes(isSecondHalf ? 30 : 0);

        // If there is already a selected date, in order to mimic the popup behavior, we need to dismiss the previous selection first.
        // Next click will be a new selection.

        // Raise the events directly here instead of DP to not lose pointer position.
        if (clickedDateTime == SelectedDateTime || SelectedDateTime != null)
        {
            SelectedDateTime = null;
        }
        else
        {
            SelectedDateTime = clickedDateTime;
            TimelineCellSelected?.Invoke(this, new TimelineCellSelectedArgs(clickedDateTime, touchPoint, positionerPoint, cellSize));
        }

        Debug.WriteLine($"Clicked: {clickedDateTime}");
    }

    public WinoDayTimelineCanvas()
    {
        DefaultStyleKey = typeof(WinoDayTimelineCanvas);
    }

    private static void OnRenderingPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WinoDayTimelineCanvas control)
        {
            control.ForceDraw();
        }
    }

    private void ForceDraw() => Canvas?.Invalidate();

    private bool CanDrawTimeline()
    {
        return RenderOptions != null
            && Canvas != null
            && WorkingHourCellBackgroundColor != null
            && SeperatorColor != null
            && HalfHourSeperatorColor != null
            && SelectedCellBackgroundBrush != null;
    }

    private void OnCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        if (!CanDrawTimeline()) return;

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        int hours = 24;

        double canvasWidth = e.Info.Width;
        double canvasHeight = e.Info.Height;

        if (canvasWidth == 0 || canvasHeight == 0) return;

        // Calculate the width of each rectangle (1 day column)
        // Equal distribution of the whole width.
        double rectWidth = canvasWidth / RenderOptions.TotalDayCount;

        // Calculate the height of each rectangle (1 hour row)
        double rectHeight = RenderOptions.CalendarSettings.HourHeight;

        // Define stroke and fill colors
        var strokeColor = ToSKColor(SeperatorColor.Color);
        float strokeThickness = 0.5f;

        // Create paints for drawing
        using var strokePaint = new SKPaint
        {
            Color = strokeColor,
            StrokeWidth = strokeThickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var dashedPaint = new SKPaint
        {
            Color = ToSKColor(HalfHourSeperatorColor.Color),
            StrokeWidth = strokeThickness,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash([2f, 2f], 0),
            IsAntialias = true
        };

        for (int day = 0; day < RenderOptions.TotalDayCount; day++)
        {
            var currentDay = RenderOptions.DateRange.StartDate.AddDays(day);

            bool isWorkingDay = RenderOptions.CalendarSettings.WorkingDays.Contains(currentDay.DayOfWeek);

            // Loop through each hour (rows)
            for (int hour = 0; hour < hours; hour++)
            {
                var renderTime = TimeSpan.FromHours(hour);

                var representingDateTime = currentDay.AddHours(hour);

                // Calculate the position and size of the rectangle
                float x = (float)(day * rectWidth);
                float y = (float)(hour * rectHeight);
                float width = (float)rectWidth;
                float height = (float)rectHeight;

                var rectangle = new SKRect(x, y, x + width, y + height);

                // Draw the rectangle border.
                // This is the main rectangle.
                canvas.DrawRect(rectangle, strokePaint);

                // Fill another rectangle with the working hour background color
                // This rectangle must be placed with -1 margin to prevent invisible borders of the main rectangle.
                if (isWorkingDay && renderTime >= RenderOptions.CalendarSettings.WorkingHourStart && renderTime <= RenderOptions.CalendarSettings.WorkingHourEnd)
                {
                    var backgroundRectangle = new SKRect(x + 1, y + 1, x + width - 1, y + height - 1);

                    canvas.DrawRect(backgroundRectangle, strokePaint);
                    fillPaint.Color = ToSKColor(WorkingHourCellBackgroundColor.Color);
                    canvas.DrawRect(backgroundRectangle, fillPaint);
                }

                // Draw a line in the center of the rectangle for representing half hours.
                float lineY = y + height / 2;
                canvas.DrawLine(x, lineY, x + width, lineY, dashedPaint);
            }

            // Draw selected item background color for the date if possible.
            if (SelectedDateTime != null)
            {
                var selectedDateTime = SelectedDateTime.Value;
                if (selectedDateTime.Date == currentDay.Date)
                {
                    var selectionRectHeight = rectHeight / 2;
                    var selectedY = selectedDateTime.Hour * rectHeight + (selectedDateTime.Minute / 60) * rectHeight;

                    // Second half of the hour is selected.
                    if (selectedDateTime.TimeOfDay.Minutes == 30)
                    {
                        selectedY += rectHeight / 2;
                    }

                    var selectedRectangle = new SKRect(
                        (float)(day * rectWidth),
                        (float)selectedY,
                        (float)(day * rectWidth + rectWidth),
                        (float)(selectedY + selectionRectHeight));

                    fillPaint.Color = ToSKColor(SelectedCellBackgroundBrush.Color);
                    canvas.DrawRect(selectedRectangle, fillPaint);
                }
            }
        }
    }

    private static SKColor ToSKColor(Windows.UI.Color color)
    {
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    public void Dispose()
    {
        if (Canvas == null) return;

        Canvas.PaintSurface -= OnCanvasPaintSurface;
        Canvas.PointerPressed -= OnCanvasPointerPressed;

        Canvas = null;
    }
}
