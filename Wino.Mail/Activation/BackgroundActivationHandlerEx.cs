using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Wino.Core.Domain.Interfaces;

namespace Wino.Activation
{
    internal class BackgroundActivationHandlerEx : ActivationHandler<BackgroundActivatedEventArgs>
    {
        private readonly IWinoServerConnectionManager<AppServiceConnection> _winoServerConnectionManager;

        public BackgroundActivationHandlerEx(IWinoServerConnectionManager<AppServiceConnection> winoServerConnectionManager)
        {
            _winoServerConnectionManager = winoServerConnectionManager;
        }

        protected override Task HandleInternalAsync(BackgroundActivatedEventArgs args)
        {
            if (args.TaskInstance == null || args.TaskInstance.TriggerDetails == null) return Task.CompletedTask;

            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails appServiceTriggerDetails)
            {
                // only accept connections from callers in the same package
                if (appServiceTriggerDetails.CallerPackageFamilyName == Package.Current.Id.FamilyName)
                {
                    // Connection established from the fulltrust process
                    _winoServerConnectionManager.Connection = appServiceTriggerDetails.AppServiceConnection;

                    var deferral = args.TaskInstance.GetDeferral();

                    args.TaskInstance.Canceled += App.Current.OnBackgroundTaskCanceled;

                    // AppServiceConnected?.Invoke(this, args.TaskInstance.TriggerDetails as AppServiceTriggerDetails);
                }
            }

            return Task.CompletedTask;
        }
    }
}
