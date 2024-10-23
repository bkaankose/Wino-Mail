using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views
{
    public sealed partial class AppShell : AppShellAbstract,
        IRecipient<ClickCalendarDateMessage>
    {
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
    }
}
