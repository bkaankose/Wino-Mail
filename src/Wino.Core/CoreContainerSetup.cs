using Wino.Core.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Wino.Authentication;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Providers;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Errors;
using Wino.Services;

namespace Wino.Core;

public static class CoreContainerSetup
{
    public static void RegisterCoreServices(this IServiceCollection services)
    {
        var loggerLevelSwitcher = new LoggingLevelSwitch();

        services.AddSingleton(loggerLevelSwitcher);
        services.AddSingleton<IMailProviderRegistry>(provider => new MailProviderRegistry(() => provider.GetServices<IMailProviderModule>()));
        services.AddSingleton<ISynchronizerFactory, SynchronizerFactory>();

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
        // The sync coordinator depends on this manager, so the manager resolves it on first use.
        services.AddSingleton(provider => new SynchronizationManager(
            provider.GetRequiredService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            () => provider.GetRequiredService<ISyncCoordinator>()));
        services.AddSingleton<ISynchronizationManager>(provider => provider.GetRequiredService<SynchronizationManager>());
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
        services.AddSingleton<IAppModeReadinessService, AppModeReadinessService>();
        services.AddTransient<IDraftSaveService, DraftSaveService>();
        services.AddSingleton<IDraftUpdateCoordinator, DraftUpdateCoordinator>();
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

        // Gmail before Outlook keeps the relay registration builders in their earlier order.
        services.AddGmailProvider();
        services.AddOutlookProvider();
        services.AddImapProvider();
        services.AddExchangeProvider();
        services.AddDavProvider();
    }

    /// <summary>
    /// Selects the loopback (desktop browser) Google authorization receiver. It replaces any earlier
    /// receiver registration, so a head can state its choice after <see cref="RegisterCoreServices"/>.
    /// </summary>
    public static IServiceCollection AddLoopbackGoogleAuthorizationCodeReceiver(this IServiceCollection services)
        => services.Replace(ServiceDescriptor.Transient<IGoogleAuthorizationCodeReceiver>(GmailProviderRegistration.CreateLoopbackGoogleAuthorizationCodeReceiver));
}
