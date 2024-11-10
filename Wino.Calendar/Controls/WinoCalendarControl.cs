using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Args;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarControl : Control
    {
        private const string PART_WinoFlipView = nameof(PART_WinoFlipView);

        public event EventHandler<TimelineCellSelectedArgs> TimelineCellSelected;
        public event EventHandler<TimelineCellUnselectedArgs> TimelineCellUnselected;

        #region Dependency Properties

        public static readonly DependencyProperty DayRangesProperty = DependencyProperty.Register(nameof(DayRanges), typeof(ObservableCollection<DayRangeRenderModel>), typeof(WinoCalendarControl), new PropertyMetadata(null));
        public static readonly DependencyProperty SelectedFlipViewIndexProperty = DependencyProperty.Register(nameof(SelectedFlipViewIndex), typeof(int), typeof(WinoCalendarControl), new PropertyMetadata(-1));
        public static readonly DependencyProperty SelectedFlipViewDayRangeProperty = DependencyProperty.Register(nameof(SelectedFlipViewDayRange), typeof(DayRangeRenderModel), typeof(WinoCalendarControl), new PropertyMetadata(null));

        public DayRangeRenderModel SelectedFlipViewDayRange
        {
            get { return (DayRangeRenderModel)GetValue(SelectedFlipViewDayRangeProperty); }
            set { SetValue(SelectedFlipViewDayRangeProperty, value); }
        }

        /// <summary>
        /// Gets or sets the collection of day ranges to render.
        /// Each day range usually represents a week, but it may support other ranges.
        /// </summary>
        public ObservableCollection<DayRangeRenderModel> DayRanges
        {
            get { return (ObservableCollection<DayRangeRenderModel>)GetValue(DayRangesProperty); }
            set { SetValue(DayRangesProperty, value); }
        }

        public int SelectedFlipViewIndex
        {
            get { return (int)GetValue(SelectedFlipViewIndexProperty); }
            set { SetValue(SelectedFlipViewIndexProperty, value); }
        }

        #endregion

        private WinoDayTimelineCanvas _activeCanvas;

        public WinoDayTimelineCanvas ActiveCanvas
        {
            get { return _activeCanvas; }
            set
            {
                // FlipView's timeline is changing.
                // Make sure to unregister from the old one.

                if (_activeCanvas != null)
                {
                    // Dismiss any selection on the old canvas.

                    _activeCanvas.SelectedDateTime = null;
                    _activeCanvas.TimelineCellSelected -= ActiveTimelineCellSelected;
                    _activeCanvas.TimelineCellUnselected -= ActiveTimelineCellUnselected;
                }

                _activeCanvas = value;

                if (_activeCanvas != null)
                {
                    _activeCanvas.TimelineCellSelected += ActiveTimelineCellSelected;
                    _activeCanvas.TimelineCellUnselected += ActiveTimelineCellUnselected;

                    // Raise visible date range change to shell.
                    WeakReferenceMessenger.Default.Send(new VisibleDateRangeChangedMessage(_activeCanvas.RenderOptions.DateRange));
                }
            }
        }

        private WinoCalendarFlipView InternalFlipView;

        public WinoCalendarControl()
        {
            DefaultStyleKey = typeof(WinoCalendarControl);
            SizeChanged += CalendarSizeChanged;
        }

        private void CalendarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActiveCanvas == null) return;

            ActiveCanvas.SelectedDateTime = null;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            InternalFlipView = GetTemplateChild(PART_WinoFlipView) as WinoCalendarFlipView;

            // Each FlipViewItem will have 1 timeline canvas to draw hour cells in the background that supports selection of them.
            // When the selection changes, we need to stop listening to the old canvas and start listening to the new one to catch events.

            InternalFlipView.ActiveTimelineCanvasChanged += FlipViewsActiveTimelineCanvasChanged;
        }

        private void FlipViewsActiveTimelineCanvasChanged(object sender, WinoDayTimelineCanvas e)
        {
            ActiveCanvas = e;

            SelectedFlipViewDayRange = InternalFlipView.SelectedItem as DayRangeRenderModel;
        }

        private void ActiveTimelineCellUnselected(object sender, TimelineCellUnselectedArgs e)
            => TimelineCellUnselected?.Invoke(this, e);

        private void ActiveTimelineCellSelected(object sender, TimelineCellSelectedArgs e)
            => TimelineCellSelected?.Invoke(this, e);

        public void NavigateToDay(DateTime dateTime) => InternalFlipView.NavigateToDay(dateTime);

        public void ResetTimelineSelection()
        {
            if (ActiveCanvas == null) return;

            ActiveCanvas.SelectedDateTime = null;
        }

        public void GoNextRange()
        {
            if (InternalFlipView == null) return;

            InternalFlipView.GoNextFlip();
        }

        public void GoPreviousRange()
        {
            if (InternalFlipView == null) return;

            InternalFlipView.GoPreviousFlip();
        }
    }
}
