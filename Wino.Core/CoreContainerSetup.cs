using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;

namespace Wino.Core
{
    public static class CoreContainerSetup
    {
        public static void RegisterCoreServices(this IServiceCollection services)
        {
            var loggerLevelSwitcher = new LoggingLevelSwitch();

            services.AddSingleton(loggerLevelSwitcher);
            services.AddSingleton<ISynchronizerFactory, SynchronizerFactory>();

            services.AddTransient<IGmailChangeProcessor, GmailChangeProcessor>();
            services.AddTransient<IImapChangeProcessor, ImapChangeProcessor>();
            services.AddTransient<IOutlookChangeProcessor, OutlookChangeProcessor>();
            services.AddTransient<IWinoRequestProcessor, WinoRequestProcessor>();
            services.AddTransient<IWinoRequestDelegator, WinoRequestDelegator>();
            services.AddTransient<IImapTestService, ImapTestService>();
            services.AddTransient<IAuthenticationProvider, AuthenticationProvider>();
            services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
            services.AddTransient<IFontService, FontService>();
            services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();
            services.AddTransient<IOutlookAuthenticator, OutlookAuthenticator>();
            services.AddTransient<IGmailAuthenticator, GmailAuthenticator>();
        }
    }
}
