using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views
{
    public sealed partial class AppShell : AppShellAbstract,
        IRecipient<GoToCalendarDayMessage>
    {
        private const string STATE_HorizontalCalendar = "HorizontalCalendar";
        private const string STATE_VerticalCalendar = "VerticalCalendar";

        public Frame GetShellFrame() => ShellFrame;
        public AppShell()
        {
            InitializeComponent();

            Window.Current.SetTitleBar(DragArea);

            ViewModel.DisplayTypeChanged += CalendarDisplayTypeChanged;
        }

        private void CalendarDisplayTypeChanged(object sender, Core.Domain.Enums.CalendarDisplayType e)
        {
            // Go to different states based on the display type.
            if (ViewModel.IsVerticalCalendar)
            {
                VisualStateManager.GoToState(this, STATE_VerticalCalendar, false);
            }
            else
            {
                VisualStateManager.GoToState(this, STATE_HorizontalCalendar, false);
            }
        }

        private void ShellFrameContentNavigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {

        }

        private void BackButtonClicked(Core.UWP.Controls.WinoAppTitleBar sender, Windows.UI.Xaml.RoutedEventArgs args)
        {

        }

        public void Receive(GoToCalendarDayMessage message)
        {
            CalendarView.GoToDay(message.DateTime);
        }

        private void PreviousDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoPreviousDateRequestedMessage());

        private void NextDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoNextDateRequestedMessage());
    }
}
