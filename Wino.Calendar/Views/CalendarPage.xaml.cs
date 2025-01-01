using System;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;
using Wino.Calendar.Args;
using Wino.Calendar.ViewModels.Messages;
using Wino.Calendar.Views.Abstract;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views
{
    public sealed partial class CalendarPage : CalendarPageAbstract,
        IRecipient<ScrollToDateMessage>,
        IRecipient<GoNextDateRequestedMessage>,
        IRecipient<GoPreviousDateRequestedMessage>,
        IRecipient<CalendarItemRightTappedMessage>
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

        private void PopupPlacementChanged(object sender, object e)
        {
            if (sender is Popup senderPopup)
            {
                // When the quick event Popup is positioned for different calendar types,
                // we must adjust the offset to make sure the tip is not hidden and has nice
                // spacing from the cell.

                switch (senderPopup.ActualPlacement)
                {
                    case PopupPlacementMode.Top:
                        senderPopup.VerticalOffset = PopupDialogOffset * -1;
                        break;
                    case PopupPlacementMode.Bottom:
                        senderPopup.VerticalOffset = PopupDialogOffset;
                        break;
                    case PopupPlacementMode.Left:
                        senderPopup.HorizontalOffset = PopupDialogOffset * -1;
                        break;
                    case PopupPlacementMode.Right:
                        senderPopup.HorizontalOffset = PopupDialogOffset;
                        break;
                    default:
                        break;
                }
            }

        }

        public void Receive(CalendarItemRightTappedMessage message)
        {

        }

        private void StartTimeDurationSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
            => ViewModel.SelectedStartTimeString = args.Text;

        private void EndTimeDurationSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
            => ViewModel.SelectedEndTimeString = args.Text;
    }
}
