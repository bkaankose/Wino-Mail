using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Metadata;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public partial class WinoServerConnectionManager : ObservableObject, IWinoServerConnectionManager<AppServiceConnection>
    {
        private AppServiceConnection _connection;

        public AppServiceConnection Connection
        {
            get { return _connection; }
            set
            {
                if (_connection != null)
                {
                    _connection.RequestReceived -= ServerMessageReceived;
                    _connection.ServiceClosed -= ServerDisconnected;
                }

                _connection = value;

                if (value == null)
                {
                    Status = WinoServerConnectionStatus.Disconnected;
                }
                else
                {
                    value.RequestReceived += ServerMessageReceived;
                    value.ServiceClosed += ServerDisconnected;

                    Status = WinoServerConnectionStatus.Connected;
                }
            }
        }

        [ObservableProperty]
        private WinoServerConnectionStatus _status;

        public async Task<bool> ConnectAsync()
        {
            if (Status == WinoServerConnectionStatus.Connected) return true;

            if (ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                try
                {
                    Status = WinoServerConnectionStatus.Connecting;

                    await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

                    // If the server connection is success, Status will be updated to Connected by BackgroundActivationHandlerEx.
                }
                catch (Exception)
                {
                    Status = WinoServerConnectionStatus.Failed;
                    return false;
                }

                return true;
            }

            return false;
        }

        public async Task<bool> DisconnectAsync()
        {
            if (Connection == null || Status == WinoServerConnectionStatus.Disconnected) return true;

            // TODO: Send disconnect message to the fulltrust process.

            return true;
        }

        public async Task InitializeAsync()
        {
            var isConnectionSuccessfull = await ConnectAsync();

            // TODO: Log connection status
        }

        private void ServerMessageReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            // TODO: Handle server messsages.
        }

        private void ServerDisconnected(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            // TODO: Handle server disconnection.
        }
    }
}
