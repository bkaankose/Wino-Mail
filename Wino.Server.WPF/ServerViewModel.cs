using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Interfaces;

namespace Wino.Server
{
    public partial class ServerViewModel : ObservableObject, IInitializeAsync
    {
        public ServerContext Context { get; }

        public ServerViewModel(ServerContext serverContext)
        {
            Context = serverContext;
        }

        [RelayCommand]
        public async Task LaunchWinoAsync()
        {
            await Context.TestOutlookSynchronizer();
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

        public Task InitializeAsync() => Context.InitializeAppServiceConnectionAsync();
    }
}
