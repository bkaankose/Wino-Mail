using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Metadata;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public partial class WinoServerConnectionManager : ObservableObject, IWinoServerConnectionManager<AppServiceConnection>
    {
        public AppServiceConnection Connection { get; set; }

        private Guid? _activeConnectionSessionId;

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

                    // await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
                }
                catch (Exception)
                {
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
    }
}
