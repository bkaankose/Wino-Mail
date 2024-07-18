using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Wino.Server.NET8
{
    public partial class App : Application
    {
        public TaskbarIcon? TrayIcon { get; private set; }
        public Window? Window { get; set; }

        public bool HandleClosedEvents { get; set; } = true;
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {

        }

        private void InitializeTrayIcon()
        {
            var showHideWindowCommand = (XamlUICommand)Resources["ShowHideWindowCommand"];
            // showHideWindowCommand.ExecuteRequested ;

            var exitApplicationCommand = (XamlUICommand)Resources["ExitApplicationCommand"];
            //exitApplicationCommand.ExecuteRequested += ExitApplicationCommand_ExecuteRequested;

            TrayIcon = (TaskbarIcon)Resources["TrayIcon"];
            TrayIcon.ForceCreate();
        }
    }
}
