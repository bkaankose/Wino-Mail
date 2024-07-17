using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wino.Server
{
    public partial class TrayIconViewModel : ObservableObject
    {
        private readonly ServerContext _context = new ServerContext();

        [RelayCommand]
        public void LaunchWino()
        {
            _context.SendTestMessageAsync();
        }

        /// <summary>
        /// Shuts down the application.
        /// </summary>
        [RelayCommand]
        public void ExitApplication()
        {
            // TODO: App service send message to UWP app to terminate itself.

            Application.Current.Shutdown();
        }

        public void Reconnect() => _context.InitializeAppServiceConnection();
    }
}
