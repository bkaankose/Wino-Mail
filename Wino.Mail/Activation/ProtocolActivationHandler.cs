using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Mvvm.Messaging;
using Windows.ApplicationModel.Activation;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Authorization;
using Wino.Messaging.Client.Shell;

namespace Wino.Activation
{
    internal class ProtocolActivationHandler : ActivationHandler<ProtocolActivatedEventArgs>
    {
        private const string GoogleAuthorizationProtocolTag = "google.pw.oauth2";
        private const string MailtoProtocolTag = "mailto:";

        private readonly INativeAppService _nativeAppService;
        private readonly ILaunchProtocolService _launchProtocolService;

        public ProtocolActivationHandler(INativeAppService nativeAppService, ILaunchProtocolService launchProtocolService)
        {
            _nativeAppService = nativeAppService;
            _launchProtocolService = launchProtocolService;
        }

        protected override Task HandleInternalAsync(ProtocolActivatedEventArgs args)
        {
            // Check URI prefix.
            var protocolString = args.Uri.AbsoluteUri;

            // Google OAuth Response
            if (protocolString.StartsWith(GoogleAuthorizationProtocolTag))
            {
                // App must be working already. No need to check for running state.
                WeakReferenceMessenger.Default.Send(new ProtocolAuthorizationCallbackReceived(args.Uri));
            }
            else if (protocolString.StartsWith(MailtoProtocolTag))
            {
                // mailto activation. Try to parse params.

                var replaced = protocolString.Replace(MailtoProtocolTag, "mailto=");
                replaced = Wino.Core.Extensions.StringExtensions.ReplaceFirst(replaced, "?", "&");

                _launchProtocolService.MailtoParameters = HttpUtility.ParseQueryString(replaced);

                if (_nativeAppService.IsAppRunning())
                {
                    // Just send publish a message. Shell will continue.
                    WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
                }
            }

            return Task.CompletedTask;
        }
    }
}
