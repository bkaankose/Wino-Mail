using System;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Calendar.Args;
using Wino.Calendar.Views.Abstract;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views
{
    public sealed partial class CalendarPage : CalendarPageAbstract,
        IRecipient<ScrollToDateMessage>,
        IRecipient<GoNextDateRequestedMessage>,
        IRecipient<GoPreviousDateRequestedMessage>
    {
        private const int PopupDialogOffset = 12;

        public CalendarPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        public void Receive(ScrollToDateMessage message) => CalendarControl.NavigateToDay(message.Date);

        public void Receive(GoNextDateRequestedMessage message) => CalendarControl.GoNextRange();

        public void Receive(GoPreviousDateRequestedMessage message) => CalendarControl.GoPreviousRange();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is CalendarPageNavigationArgs args)
            {
                if (args.RequestDefaultNavigation)
                {
                    // Go today.
                    WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(DateTime.Now.Date, CalendarInitInitiative.App));
                }
                else
                {
                    // Go specified date.
                    WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(args.NavigationDate, CalendarInitInitiative.User));
                }
            }
        }

        private void CellSelected(object sender, TimelineCellSelectedArgs e)
        {
            ViewModel.SelectedQuickEventDate = e.ClickedDate;

            TeachingTipPositionerGrid.Width = e.CellSize.Width;
            TeachingTipPositionerGrid.Height = e.CellSize.Height;

            Canvas.SetLeft(TeachingTipPositionerGrid, e.PositionerPoint.X);
            Canvas.SetTop(TeachingTipPositionerGrid, e.PositionerPoint.Y);

            // Adjust the start and end time in the flyout.
            var startTime = ViewModel.SelectedQuickEventDate.Value.TimeOfDay;
            var endTime = startTime.Add(TimeSpan.FromMinutes(30));

            ViewModel.SelectQuickEventTimeRange(startTime, endTime);

            QuickEventPopupDialog.IsOpen = true;
        }

        private void CellUnselected(object sender, TimelineCellUnselectedArgs e)
        {
            QuickEventPopupDialog.IsOpen = false;
        }

        private void QuickEventAccountSelectorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QuickEventAccountSelectorFlyout.Hide();
        }

        private void QuickEventPopupClosed(object sender, object e)
        {
            // Reset the timeline selection when the tip is closed.
            CalendarControl.ResetTimelineSelection();
        }

        private void QuickEventPopupPlacementChanged(object sender, object e)
        {
            // When the quick event Popup is positioned for different calendar types,
            // we must adjust the offset to make sure the tip is not hidden and has nice
            // spacing from the cell.

            switch (QuickEventPopupDialog.ActualPlacement)
            {
                case Windows.UI.Xaml.Controls.Primitives.PopupPlacementMode.Top:
                    QuickEventPopupDialog.VerticalOffset = PopupDialogOffset * -1;
                    break;
                case Windows.UI.Xaml.Controls.Primitives.PopupPlacementMode.Bottom:
                    QuickEventPopupDialog.VerticalOffset = PopupDialogOffset;
                    break;
                case Windows.UI.Xaml.Controls.Primitives.PopupPlacementMode.Left:
                    QuickEventPopupDialog.HorizontalOffset = PopupDialogOffset * -1;
                    break;
                case Windows.UI.Xaml.Controls.Primitives.PopupPlacementMode.Right:
                    QuickEventPopupDialog.HorizontalOffset = PopupDialogOffset;
                    break;
                default:
                    break;
            }
        }

        private void ComboBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
        {
            ViewModel.SelectedStartTimeString = args.Text;
        }
    }
}
