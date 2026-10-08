using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Integration;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Gmail;
using Wino.Core.Synchronizers.Errors.Imap;
using Wino.Core.Synchronizers.Errors.Outlook;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.CardDav;
using Wino.Services;

namespace Wino.Core;

public static class CoreContainerSetup
{
    public static void RegisterCoreServices(this IServiceCollection services)
    {
        var loggerLevelSwitcher = new LoggingLevelSwitch();

        services.AddSingleton(loggerLevelSwitcher);
        services.AddSingleton<ISynchronizerFactory, SynchronizerFactory>();
        services.AddSingleton<ModeSynchronizerFactory>();
        services.AddSingleton<IMailSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<ICalendarSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<IContactSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<ITaskSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<ISynchronizationManager>(provider => SynchronizationManager.Instance);
        services.AddTransient<IApplicationLocalRequestExecutor, ApplicationLocalRequestExecutor>();
        services.AddTransient<SynchronizationManagerInitializer>();
        services.AddSingleton(provider => new ApplicationRuntimeInitialization(
            () => provider.GetRequiredService<IDatabaseService>(),
            () => provider.GetRequiredService<IMailIntelligenceStore>(),
            () => provider.GetRequiredService<ITranslationService>(),
            () => provider.GetRequiredService<SynchronizationManagerInitializer>(),
            () => provider.GetRequiredService<IKeyboardShortcutService>(),
            () => provider.GetRequiredService<Wino.Services.AccountProfilePictureMaintenance>(),
            () => provider.GetRequiredService<Wino.Services.AccountSenderPictureDirectory>(),
            () => provider.GetRequiredService<IWinoAccountIntelligenceSnapshotService>(),
            () => provider.GetRequiredService<IMailIntelligenceCoordinator>(),
            () => provider.GetRequiredService<Wino.Services.IntelligenceResultKeyLifecycle>()));
        services.AddSingleton<IApplicationRuntime>(provider => new ApplicationRuntime(
            provider.GetRequiredService<ISynchronizationManager>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
            provider.GetRequiredService<IWinoLogger>(),
            provider.GetRequiredService<ApplicationRuntimeInitialization>().InitializeAsync,
            provider.GetRequiredService<ApplicationRuntimeInitialization>().RunBackgroundStartupAsync));

        services.AddTransient<IGmailChangeProcessor, GmailChangeProcessor>();
        services.AddTransient<IImapChangeProcessor, ImapChangeProcessor>();
        services.AddTransient<IOutlookChangeProcessor, OutlookChangeProcessor>();
        services.AddTransient<IWinoRequestProcessor, WinoRequestProcessor>();
        services.AddTransient<IWinoRequestDelegator, WinoRequestDelegator>();
        services.AddTransient<IMailFilterExecutor, MailFilterExecutor>();
        services.AddTransient<IMailFilterProviderService, MailFilterProviderService>();
        services.AddTransient<IProviderFeatureAuthorizationService, ProviderFeatureAuthorizationService>();
        services.AddTransient<IMigrationAccountAuthorizationService, MigrationAccountAuthorizationService>();
        services.AddTransient<IAccountReauthenticationService, AccountReauthenticationService>();
        services.AddTransient<IAccountCapabilityService, AccountCapabilityService>();
        services.AddSingleton<IAppModeReadinessService, AppModeReadinessService>();
        services.AddTransient<IDraftSaveService, DraftSaveService>();
        services.AddSingleton<IDraftUpdateCoordinator, DraftUpdateCoordinator>();
        services.AddTransient<IDraftSyncRetryService, DraftSyncRetryService>();
        services.AddTransient<IMailServerTestService, MailServerTestService>();
        services.AddTransient<IPop3ClientFactory, MailKitPop3ClientFactory>();
        services.AddTransient<ISmtpTransport, MailKitSmtpTransport>();
        services.AddTransient<IAuthenticationProvider>(provider => new AuthenticationProvider(
            () => provider.GetRequiredService<IOutlookAuthenticator>(),
            () => provider.GetRequiredService<IGmailAuthenticator>()));
        services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
        services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();
        services.AddTransient<IOutlookAuthenticator, OutlookAuthenticator>();
        services.AddTransient<IGmailAuthenticator, GmailAuthenticator>();

        services.AddTransient<UnifiedImapSynchronizer>();
        services.AddTransient<ICardDavSynchronizationEngine, CardDavSynchronizationEngine>();
        services.AddTransient<ICardDavAddressBookService, CardDavAddressBookService>();
        services.AddTransient<ICardDavContactListService, CardDavContactListService>();

        // Register Outlook error handlers
        services.AddTransient<ObjectCannotBeDeletedHandler>();
        services.AddTransient<DeltaTokenExpiredHandler>();
        services.AddTransient<OutlookRateLimitHandler>();

        // Register Gmail error handlers
        services.AddTransient<GmailAuthenticationFailedHandler>();
        services.AddTransient<GmailQuotaExceededHandler>();
        services.AddTransient<GmailRateLimitHandler>();
        services.AddTransient<GmailHistoryExpiredHandler>();
        // Register shared error handlers
        services.AddTransient<EntityNotFoundHandler>();

        // Register IMAP error handlers
        services.AddTransient<ImapConnectionLostHandler>();
        services.AddTransient<ImapAuthenticationFailedHandler>();
        services.AddTransient<ImapFolderNotFoundHandler>();
        services.AddTransient<ImapProtocolErrorHandler>();

        // Register Outlook auth handlers
        services.AddTransient<OutlookAuthenticationFailedHandler>();

        // Register error handler factories
        services.AddTransient<IOutlookSynchronizerErrorHandlerFactory, OutlookSynchronizerErrorHandlingFactory>();
        services.AddTransient<IGmailSynchronizerErrorHandlerFactory, GmailSynchronizerErrorHandlingFactory>();
        services.AddTransient<IImapSynchronizerErrorHandlerFactory, ImapSynchronizerErrorHandlingFactory>();

        // Register retry executor
        services.AddTransient<IRetryExecutor, RetryExecutor>();
    }
}
