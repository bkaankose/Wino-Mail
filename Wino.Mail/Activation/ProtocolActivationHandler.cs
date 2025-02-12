using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Windows.ApplicationModel.Activation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Launch;
using Wino.Messaging.Client.Shell;

namespace Wino.Activation
{
    internal class ProtocolActivationHandler : ActivationHandler<ProtocolActivatedEventArgs>
    {
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

            if (protocolString.StartsWith(MailtoProtocolTag))
            {
                // mailto activation. Try to parse params.
                _launchProtocolService.MailToUri = new MailToUri(protocolString);

                if (_nativeAppService.IsAppRunning())
                {
                    // Just send publish a message. Shell will continue.
                    WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
                }
            }

            return Task.CompletedTask;
        }

        protected override bool CanHandleInternal(ProtocolActivatedEventArgs args)
        {
            // Validate the URI scheme.

            try
            {
                var uriGet = args.Uri;
            }
            catch (UriFormatException)
            {
                return false;
            }

            return base.CanHandleInternal(args);
        }
    }
}
