using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel;
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
            //var opt = new SynchronizationOptions()
            //{
            //    Type = Wino.Core.Domain.Enums.SynchronizationType.Full,
            //    AccountId = Guid.Parse("b3620ce7-8a69-4d81-83d5-a94bbe177431")
            //};

            //var req = new NewSynchronizationRequested(opt, Wino.Core.Domain.Enums.SynchronizationSource.Server);
            //WeakReferenceMessenger.Default.Send(req);

            // return Task.CompletedTask;

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
        public async Task ExitApplication()
        {
            // Find the running UWP app by AppDiagnosticInfo API and terminate it if possible.
            var appDiagnosticInfos = await AppDiagnosticInfo.RequestInfoForPackageAsync(Package.Current.Id.FamilyName);

            var clientDiagnosticInfo = appDiagnosticInfos.FirstOrDefault();

            if (clientDiagnosticInfo == null)
            {
                Debug.WriteLine($"Wino Mail client is not running. Termination is skipped.");
            }
            else
            {
                var appResourceGroupInfo = clientDiagnosticInfo.GetResourceGroups().FirstOrDefault();

                if (appResourceGroupInfo != null)
                {
                    await appResourceGroupInfo.StartTerminateAsync();

                    Debug.WriteLine($"Wino Mail client is terminated succesfully.");
                }
            }

            Application.Current.Shutdown();
        }

        public async Task ReconnectAsync() => await Context.InitializeAppServiceConnectionAsync();

        public Task InitializeAsync() => Context.InitializeAppServiceConnectionAsync();
    }
}
