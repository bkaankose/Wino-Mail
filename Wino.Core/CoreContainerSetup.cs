using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Core.Authenticators;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Integration.Threading;
using Wino.Core.Services;

namespace Wino.Core
{
    public static class CoreContainerSetup
    {
        public static void RegisterCoreServices(this IServiceCollection services)
        {
            var loggerLevelSwitcher = new LoggingLevelSwitch();

            services.AddSingleton(loggerLevelSwitcher);
            services.AddSingleton<ILogInitializer, LogInitializer>();

            services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
            services.AddSingleton<ITranslationService, TranslationService>();
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<IThreadingStrategyProvider, ThreadingStrategyProvider>();
            services.AddSingleton<IMimeFileService, MimeFileService>();

            services.AddTransient<IGmailChangeProcessor, GmailChangeProcessor>();
            services.AddTransient<IImapChangeProcessor, ImapChangeProcessor>();
            services.AddTransient<IOutlookChangeProcessor, OutlookChangeProcessor>();

            services.AddTransient<ITokenService, TokenService>();
            services.AddTransient<IProviderService, ProviderService>();
            services.AddTransient<IFolderService, FolderService>();
            services.AddTransient<IMailService, MailService>();
            services.AddTransient<IAccountService, AccountService>();
            services.AddTransient<IContactService, ContactService>();
            services.AddTransient<ISignatureService, SignatureService>();
            services.AddTransient<IWinoRequestProcessor, WinoRequestProcessor>();
            services.AddTransient<IWinoRequestDelegator, WinoRequestDelegator>();
            services.AddTransient<IImapTestService, ImapTestService>();
            services.AddTransient<IAuthenticationProvider, AuthenticationProvider>();
            services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
            services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
            services.AddTransient<IFontService, FontService>();
            services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();

            services.AddTransient<IOutlookAuthenticator, OutlookAuthenticator>();
            services.AddTransient<IGmailAuthenticator, GmailAuthenticator>();

            services.AddTransient<OutlookThreadingStrategy>();
            services.AddTransient<GmailThreadingStrategy>();
            services.AddTransient<ImapThreadStrategy>();

            services.AddSingleton<ISynchronizerFactory, SynchronizerFactory>();
        }
    }
}
