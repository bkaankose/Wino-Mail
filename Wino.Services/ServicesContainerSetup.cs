using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Domain.Interfaces;
using Wino.Services.Authenticators;
using Wino.Services.Processors;
using Wino.Services.Services;
using Wino.Services.Threading;

namespace Wino.Services
{
    public static class ServicesContainerSetup
    {
        public static void RegisterServices(this IServiceCollection services)
        {
            var loggerLevelSwitcher = new LoggingLevelSwitch();

            services.AddSingleton(loggerLevelSwitcher);
            services.AddSingleton<ILogInitializer, LogInitializer>();

            services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
            services.AddSingleton<ITranslationService, TranslationService>();
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<IThreadingStrategyProvider, ThreadingStrategyProvider>();
            services.AddSingleton<IMimeFileService, MimeFileService>();

            services.AddTransient<ITokenService, TokenService>();
            services.AddTransient<IProviderService, ProviderService>();
            services.AddTransient<IFolderService, FolderService>();
            services.AddTransient<IMailService, MailService>();
            services.AddTransient<IAccountService, AccountService>();
            services.AddTransient<IContactService, ContactService>();
            services.AddTransient<ISignatureService, SignatureService>();

            services.AddTransient<IWinoRequestDelegator, WinoRequestDelegator>();
            services.AddTransient<IWinoRequestProcessor, WinoRequestProcessor>();

            services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
            services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
            services.AddTransient<IFontService, FontService>();
            services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();
            services.AddTransient<IHtmlPreviewer, HtmlPreviewer>();

            services.AddTransient<IOutlookAuthenticator, OutlookAuthenticator>();
            services.AddTransient<IGmailAuthenticator, GmailAuthenticator>();
            services.AddTransient<IAuthenticationProvider, AuthenticationProvider>();

            services.AddTransient<IGmailChangeProcessor, GmailChangeProcessor>();
            services.AddTransient<IImapChangeProcessor, ImapChangeProcessor>();
            services.AddTransient<IOutlookChangeProcessor, OutlookChangeProcessor>();

            services.AddTransient<IOutlookThreadingStrategy, OutlookThreadingStrategy>();
            services.AddTransient<IGmailThreadingStrategy, GmailThreadingStrategy>();
            services.AddTransient<IImapThreadStrategy, ImapThreadStrategy>();
        }
    }
}
