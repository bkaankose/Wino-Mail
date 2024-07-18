using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wino.Server
{
    public partial class TrayIconViewModel : ObservableObject
    {
        public ServerContext Context { get; }

        public TrayIconViewModel(ServerContext serverContext)
        {
            Context = serverContext;
        }

        [RelayCommand]
        public void LaunchWino()
        {

            // ServerContext.SendTestMessageAsync();
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

        public async Task ReconnectAsync() => await Context.InitializeAppServiceConnectionAsync();
    }
}
