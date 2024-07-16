using System.Windows;
using H.NotifyIcon;

namespace Wino.Server
{
    public partial class App : Application
    {
        private TaskbarIcon? notifyIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");
            notifyIcon.ForceCreate(enablesEfficiencyMode: true);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
