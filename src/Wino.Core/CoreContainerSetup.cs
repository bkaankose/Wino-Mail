using Wino.Core.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Providers;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Errors;
using Wino.Services;
using Wino.Services.Interfaces;

namespace Wino.Core;

public static class CoreContainerSetup
{
    public static void RegisterCoreServices(this IServiceCollection services)
    {
        var loggerLevelSwitcher = new LoggingLevelSwitch();

        services.AddSingleton(loggerLevelSwitcher);
        services.AddSingleton<IMailProviderRegistry>(provider => new MailProviderRegistry(() => provider.GetServices<IMailProviderModule>()));
        services.AddSingleton<SynchronizerFactory>();
        services.AddSingleton<ITaskTransferService, TaskTransferService>();
        services.AddSingleton<ISynchronizerFactory>(provider => provider.GetRequiredService<SynchronizerFactory>());

        // Relay registrations the app manages itself (Gmail watch, Graph subscription). IMAP's builder lives in Wino.Services.
        services.AddSingleton<Wino.Core.PushRelay.PushRelayAppManagedRegistrationStore>();
        services.AddSingleton<IPushRelayAppManagedRegistrationStore>(provider => provider.GetRequiredService<Wino.Core.PushRelay.PushRelayAppManagedRegistrationStore>());
        services.AddSingleton<Wino.Core.PushRelay.RelayWellKnownCache>(provider => new Wino.Core.PushRelay.RelayWellKnownCache(provider.GetRequiredService<IPushRelayClient>()));
        services.AddSingleton<ModeSynchronizerFactory>();
        services.AddSingleton<IMailSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<ICalendarSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<IContactSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<ITaskSynchronizerFactory>(provider => provider.GetRequiredService<ModeSynchronizerFactory>());
        services.AddSingleton<RequestUiChangeCoordinator>();
        services.AddSingleton(provider => new SynchronizationManager(
            provider.GetRequiredService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            provider.GetRequiredService<IAppLockService>(),
            provider.GetRequiredService<SynchronizerFactory>(),
            provider.GetRequiredService<IMailServerTestService>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetRequiredService<INotificationBuilder>(),
            provider.GetRequiredService<IAuthenticationProvider>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<IDraftSyncRetryService>(),
            provider.GetRequiredService<IMailService>(),
            provider.GetRequiredService<Wino.Services.Synchronization.TaskQuotaGate>(),
            provider.GetRequiredService<IWinoLogger>(),
            provider.GetRequiredService<Wino.Core.Domain.Models.MailItem.DraftUpdateRegistry>(),
            provider.GetRequiredService<IFolderService>(),
            provider.GetService<IPlatformCapabilities>()));
        services.AddSingleton<ISynchronizationManager>(provider => provider.GetRequiredService<SynchronizationManager>());
        services.AddSingleton<IDraftUpdateCoordinator>(provider => provider.GetRequiredService<SynchronizationManager>().DraftUpdates);
        services.AddTransient<IApplicationLocalRequestExecutor, ApplicationLocalRequestExecutor>();
        services.AddSingleton(provider => new ApplicationRuntimeInitialization(
            () => provider.GetRequiredService<IDatabaseService>(),
            () => provider.GetRequiredService<IMailIntelligenceStore>(),
            () => provider.GetRequiredService<ITranslationService>(),
            () => provider.GetRequiredService<IKeyboardShortcutService>(),
            () => provider.GetRequiredService<Wino.Services.AccountProfilePictureMaintenance>(),
            () => provider.GetRequiredService<Wino.Services.AccountSenderPictureDirectory>(),
            () => provider.GetRequiredService<IWinoAccountIntelligenceSnapshotService>(),
            () => provider.GetRequiredService<IMailIntelligenceCoordinator>(),
            () => provider.GetRequiredService<Wino.Services.IntelligenceResultKeyLifecycle>(),
            () => provider.GetRequiredService<IPushRelayRegistrationService>(),
            () => provider.GetService<IRelaySocketCoordinator>(),
            () => provider.GetRequiredService<ISyncCoordinator>(),
            () => provider.GetService<IAppPolicyAcceptanceSyncService>(),
            () => provider.GetServices<IStartupInitializer>(),
            () => provider.GetService<ICredentialStoreMigrationService>()));
        services.AddSingleton<IApplicationRuntime>(provider => new ApplicationRuntime(
            provider.GetRequiredService<ISynchronizationManager>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<IPlatformCapabilities>(),
            provider.GetRequiredService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
            provider.GetRequiredService<IWinoLogger>(),
            provider.GetRequiredService<ApplicationRuntimeInitialization>().InitializeAsync,
            provider.GetRequiredService<ApplicationRuntimeInitialization>().RunBackgroundStartupAsync,
            provider.GetRequiredService<ISyncCoordinator>()));

        services.AddTransient<IWinoRequestProcessor, WinoRequestProcessor>();
        services.AddTransient<IWinoRequestDelegator, WinoRequestDelegator>();
        // One executor per app: the platform sets its window host on it (AgentPlanExecutor.Host) when the shell is up.
        services.AddSingleton<Services.Assistant.AgentPlanExecutor>();
        services.AddSingleton<Wino.Agent.Runtime.Ports.IAgentPlanExecutor>(provider => provider.GetRequiredService<Services.Assistant.AgentPlanExecutor>());
        services.AddTransient<IMailFilterExecutor, MailFilterExecutor>();
        services.AddTransient<IMailFilterProviderService, MailFilterProviderService>();
        services.AddTransient<IProviderFeatureAuthorizationService, ProviderFeatureAuthorizationService>();
        services.AddTransient<IMigrationAccountAuthorizationService, MigrationAccountAuthorizationService>();
        services.AddTransient<IAccountReauthenticationService, AccountReauthenticationService>();
        services.AddTransient<IAccountCapabilityService, AccountCapabilityService>();
        services.AddSingleton<IMailSynchronizationRangeService, MailSynchronizationRangeService>();
        services.AddSingleton<IAppModeReadinessService, AppModeReadinessService>();
        services.AddTransient<IDraftSaveService, DraftSaveService>();
        services.AddTransient<IDraftSyncRetryService, DraftSyncRetryService>();
        services.AddTransient<IAuthenticationProvider, AuthenticationProvider>();
        services.AddSingleton<IDnsLookupService, DnsLookupService>();
        services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
        services.AddTransient<IAccountRouteResolver, AccountRouteResolver>();
        services.AddTransient<IAccountSetupRunner, AccountSetupRunner>();
        services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();
        services.AddTransient<IMailInvitationService, MailInvitationService>();
        services.AddWinoOAuth();

        services.AddTransient<EntityNotFoundHandler>();
        services.AddTransient<IRetryExecutor, RetryExecutor>();

        // The ICS calendar store that CalDAV, local calendars and mail invitations write through for every provider.
        services.AddTransient<ICalDavChangeProcessor, CalDavChangeProcessor>();

        // Wino.Core references no provider project. Every host registers the providers directly after this call,
        // Gmail before Outlook, so the relay registration builders keep their order.
    }
}
