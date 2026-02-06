using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Errors.Gmail;
using Wino.Core.Synchronizers.Errors.Imap;
using Wino.Core.Synchronizers.Errors.Outlook;
using Wino.Core.Synchronizers.ImapSync;

namespace Wino.Core;

public static class CoreContainerSetup
{
    public static void RegisterCoreServices(this IServiceCollection services)
    {
        var loggerLevelSwitcher = new LoggingLevelSwitch();

        services.AddSingleton(loggerLevelSwitcher);
        services.AddSingleton<ISynchronizerFactory, SynchronizerFactory>();
        services.AddSingleton<ISynchronizationManager>(provider => SynchronizationManager.Instance);
        services.AddTransient<SynchronizationManagerInitializer>();

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

        services.AddTransient<IImapSynchronizationStrategyProvider, ImapSynchronizationStrategyProvider>();
        services.AddTransient<CondstoreSynchronizer>();
        services.AddTransient<QResyncSynchronizer>();
        services.AddTransient<UidBasedSynchronizer>();
        services.AddTransient<UnifiedImapSynchronizer>();

        // Register Outlook error handlers
        services.AddTransient<ObjectCannotBeDeletedHandler>();
        services.AddTransient<DeltaTokenExpiredHandler>();

        // Register Gmail error handlers
        services.AddTransient<GmailQuotaExceededHandler>();
        services.AddTransient<GmailRateLimitHandler>();
        services.AddTransient<GmailHistoryExpiredHandler>();

        // Register IMAP error handlers
        services.AddTransient<ImapConnectionLostHandler>();
        services.AddTransient<ImapAuthenticationFailedHandler>();
        services.AddTransient<ImapFolderNotFoundHandler>();
        services.AddTransient<ImapProtocolErrorHandler>();

        // Register error handler factories
        services.AddTransient<IOutlookSynchronizerErrorHandlerFactory, OutlookSynchronizerErrorHandlingFactory>();
        services.AddTransient<IGmailSynchronizerErrorHandlerFactory, GmailSynchronizerErrorHandlingFactory>();
        services.AddTransient<IImapSynchronizerErrorHandlerFactory, ImapSynchronizerErrorHandlingFactory>();

        // Register retry executor
        services.AddTransient<IRetryExecutor, RetryExecutor>();
    }
}
