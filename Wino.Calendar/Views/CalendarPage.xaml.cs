﻿using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Args;
using Wino.Calendar.Views.Abstract;

namespace Wino.Calendar.Views
{
    public sealed partial class CalendarPage : CalendarPageAbstract
    {
        public CalendarPage()
        {
            InitializeComponent();
        }

        private void CellSelected(object sender, TimelineCellSelectedArgs e)
        {
            TeachingTipPositionerGrid.Width = e.CellSize.Width;
            TeachingTipPositionerGrid.Height = e.CellSize.Height;

            Canvas.SetLeft(TeachingTipPositionerGrid, e.PositionerPoint.X);
            Canvas.SetTop(TeachingTipPositionerGrid, e.PositionerPoint.Y);

            //var t = new Flyout()
            //{
            //    Content = new TextBlock() { Text = "Create event" }
            //};

            //t.ShowAt(TeachingTipPositionerGrid, new FlyoutShowOptions()
            //{
            //    ShowMode = FlyoutShowMode.Transient,
            //    Placement = FlyoutPlacementMode.Right
            //});

            NewEventTip.IsOpen = true;
        }

        private void CellUnselected(object sender, TimelineCellUnselectedArgs e)
        {
            NewEventTip.IsOpen = false;
        }

        private void CreateEventTipClosed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            // Reset the timeline selection when the tip is closed.
            CalendarControl.ResetTimelineSelection();
        }
    }
}