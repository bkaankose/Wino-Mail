using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Server
{
    public partial class ServerViewModel : ObservableObject, IInitializeAsync
    {
        private readonly INotificationBuilder _notificationBuilder;

        public ServerContext Context { get; }

        public ServerViewModel(ServerContext serverContext, INotificationBuilder notificationBuilder)
        {
            Context = serverContext;
            _notificationBuilder = notificationBuilder;
        }

        [RelayCommand]
        public Task LaunchWinoAsync()
        {
            return Launcher.LaunchUriAsync(new Uri($"{App.WinoMailLaunchProtocol}:")).AsTask();
            //await _notificationBuilder.CreateNotificationsAsync(Guid.Empty, new List<IMailItem>()
            //{
            //    new MailCopy(){  UniqueId = Guid.Parse("8f25d2a0-4448-4fee-96a9-c9b25a19e866")}
            //});
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
