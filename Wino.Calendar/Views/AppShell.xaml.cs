using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views
{
    public sealed partial class AppShell : AppShellAbstract,
        IRecipient<ClickCalendarDateMessage>,
        IRecipient<CalendarDisplayModeChangedMessage>
    {
        private const string STATE_HorizontalCalendar = "HorizontalCalendar";
        private const string STATE_VerticalCalendar = "VerticalCalendar";

        public Frame GetShellFrame() => ShellFrame;
        public AppShell()
        {
            InitializeComponent();

            Window.Current.SetTitleBar(DragArea);
        }

        private void ShellFrameContentNavigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {

        }

        private void BackButtonClicked(Core.UWP.Controls.WinoAppTitleBar sender, Windows.UI.Xaml.RoutedEventArgs args)
        {
            if (ViewModel.CurrentDisplayType == Core.Domain.Enums.CalendarDisplayType.Day)
            {
                ViewModel.CurrentDisplayType = Core.Domain.Enums.CalendarDisplayType.Month;
            }
            else
            {
                ViewModel.CurrentDisplayType = Core.Domain.Enums.CalendarDisplayType.Day;
            }
        }

        public void Receive(ClickCalendarDateMessage message)
        {
            if (CalendarView.DisplayDate == message.DateTime)
            {
                // Just scroll to it, already loaded.
                WeakReferenceMessenger.Default.Send(new ScrollToDateMessage(message.DateTime));
            }
            else
            {
                CalendarView.DisplayDate = message.DateTime;
            }
        }

        public void Receive(CalendarDisplayModeChangedMessage message)
        {
            if (ViewModel.IsVerticalCalendar)
            {
                VisualStateManager.GoToState(this, STATE_VerticalCalendar, false);
            }
            else
            {
                VisualStateManager.GoToState(this, STATE_HorizontalCalendar, false);
            }
        }

        private void PreviousDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoPreviousDateRequestedMessage());

        private void NextDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoNextDateRequestedMessage());
    }
}
