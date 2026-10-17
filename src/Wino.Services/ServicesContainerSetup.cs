using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Services.PushRelay;
using Wino.Core.Domain.Intelligence.Keys;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.AI.ContentProcessing;
using Wino.Services.CalDav;
using Wino.Services.CardDav;
using Wino.Services.Dav;
using Wino.Core.ML;

namespace Wino.Services;

public static class ServicesContainerSetup
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AddSingleton<Wino.Core.Domain.Models.MailItem.DraftUpdateRegistry>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IMailContentProjector, MailContentProjector>();
        services.AddSingleton<IDatabaseSchemaService, DatabaseSchemaService>();
        services.AddSingleton<IMigrationClock, SystemMigrationClock>();
        services.AddSingleton<IAuthenticationTokenMigrationService, AuthenticationTokenMigrationService>();
        services.AddSingleton<IMigrationCoordinator, DatabaseMigrationCoordinator>();
        services.AddSingleton<IDatabaseService, DatabaseService>();

        services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
        services.AddSingleton<IWinoTelemetryContextProvider, WinoTelemetryContextProvider>();
        services.AddSingleton<IWinoTelemetrySink, SentryWinoTelemetrySink>();
        services.AddSingleton<IWinoLogger, WinoLogger>();
        services.AddSingleton<IWinoTelemetryService, WinoTelemetryService>();
        services.AddSingleton<INotificationPolicyService, NotificationPolicyService>();
        services.AddSingleton<IActivationStateService, ActivationStateService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<IContentTypeClassificationModel, MagikaContentTypeClassificationModel>();
        services.AddSingleton<IContentTypeDetectionService, ContentTypeDetectionService>();
        services.AddSingleton<IAttachmentFileService, AttachmentFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddSingleton<IActivationFileImportService, ActivationFileImportService>();
        // Needs the platform INotificationBuilder; each head starts it once its shell is running.
        services.AddSingleton<ICalendarReminderServer, CalendarReminderServer>();
        // Needs the head's INotificationResponseNavigator (ViewModels layer) and platform INotificationBuilder.
        services.AddTransient<INotificationResponseService, Wino.Services.Notifications.NotificationResponseService>();

        services.AddTransient<ICalendarService, CalendarService>();
        services.AddTransient<IMailService, MailService>();
        services.AddTransient<IPop3PersistenceService, Pop3PersistenceService>();
        services.AddTransient<IMailCategoryService, MailCategoryService>();
        services.AddTransient<IMailFilterService, MailFilterService>();
        services.AddTransient<IAccountProviderFeatureService, AccountProviderFeatureService>();
        services.AddTransient<ISentMailReceiptService, SentMailReceiptService>();
        services.AddTransient<IFolderService, FolderService>();
        services.AddTransient<IUnreadBadgeService, UnreadBadgeService>();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IServerCertificateTrustService, ServerCertificateTrustService>();
        services.AddTransient<IContactService, ContactService>();
        services.AddTransient<IContactQueryService>(provider => provider.GetRequiredService<IContactService>());
        services.AddTransient<IRecipientHistoryService, RecipientHistoryService>();
        services.AddTransient<IRecipientSuggestionService, RecipientSuggestionService>();
        services.AddTransient<ITaskService, TaskService>();
        services.AddTransient<ITaskQueryService>(provider => provider.GetRequiredService<ITaskService>());
        services.AddTransient<ISignatureService, SignatureService>();
        services.AddTransient<IEmailTemplateService, EmailTemplateService>();
        services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
        services.AddSingleton<IKnownImapProviderCatalogLoader, KnownImapProviderCatalogLoader>();
        services.AddSingleton<IKnownImapProviderCatalog, EmbeddedKnownImapProviderCatalog>();
        services.AddTransient<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddSingleton<IKeyboardShortcutService, KeyboardShortcutService>();
        services.AddSingleton<IWinoAccountSessionService>(provider => WinoAccountSessionService.For(provider.GetRequiredService<IDatabaseService>(), provider.GetRequiredService<IAccountCredentialPersistence>()));
        services.AddSingleton<IWinoAccountApiClient, WinoAccountApiClient>();
        services.AddSingleton<IIntelligenceBackend, CloudIntelligenceBackend>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileService>();
        RegisterPushRelayServices(services);
        services.AddSingleton<IWinoBillingService, WinoBillingService>();
        services.AddSingleton<IWinoPendingCheckoutStore, WinoPendingCheckoutStore>();
        services.AddSingleton<IWinoPurchaseReconciliationService, WinoPurchaseReconciliationService>();
        services.AddSingleton<IWinoStorePurchaseRedeemService, WinoStorePurchaseRedeemService>();
        services.AddSingleton<IWinoAppStorePurchaseService, WinoAppStorePurchaseService>();
        services.AddSingleton<IWinoAccountIntelligenceSnapshotService, WinoAccountIntelligenceSnapshotService>();
        services.AddSingleton<ISemanticIndexJobRegistry, SemanticIndexJobRegistry>();
        services.AddSingleton<IIntelligenceMessageContextResolver, IntelligenceMessageContextResolver>();
        services.AddSingleton<IMailIntelligenceCoordinator, MailIntelligenceCoordinator>();
        services.AddSingleton<MailIntelligenceUploadBuilder>();
        services.AddSingleton<IWinoIntelligenceCoordinator, WinoIntelligenceCoordinator>();
        services.AddSingleton<IIntelligenceCoverageHandoff, IntelligenceCoverageHandoff>();
        services.AddSingleton<MailIntelligenceStore>();
        services.AddSingleton<IMailIntelligenceStore>(provider => provider.GetRequiredService<MailIntelligenceStore>());
        services.AddSingleton<IIntelligenceResultKeyRows>(provider => provider.GetRequiredService<MailIntelligenceStore>());
        services.AddSingleton<IntelligenceResultKeyPresence>();
        services.AddSingleton<IIntelligenceResultKeyStore, IntelligenceResultKeyStore>();
        services.AddSingleton<IntelligenceResultKeyLifecycle>();
        services.AddSingleton<IntelligenceTransportKeyProvider>();
        services.AddSingleton<MailIntelligenceResultPageReader>();
        services.AddSingleton<ILocalIntelligenceService, LocalIntelligenceService>();
        services.AddSingleton<IContentEnvelopeEncryptor>(_ =>
            new PemContentEnvelopeEncryptor(EmbeddedIntelligencePublicKeyProvider.Load()));
        services.AddSingleton<ISyncSnapshotKeyService, SyncSnapshotKeyService>();
        services.AddTransient<IWinoAccountDataSyncService, WinoAccountDataSyncService>();
        services.AddSingleton<IPictureStorageService, PictureStorageService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<AccountProfilePictureMaintenance>();
        services.AddSingleton<AccountSenderPictureDirectory>();

        services.AddSingleton<IDavTransport>(_ => new DavTransport());
        services.AddSingleton<IDavMultistatusReader, DavMultistatusReader>();
        services.AddSingleton<IDavResponseHandler, DavResponseHandler>();
        services.AddSingleton<IDavCredentialStore, DavCredentialStore>();
        services.AddSingleton<IVCardCodec, VCardCodec>();
        services.AddTransient<ICardDavSynchronizationStore, CardDavSynchronizationStore>();
        services.AddTransient<ICardDavClient, CardDavClient>();
        services.AddTransient<ICalDavClient>(provider => new CalDavClient(
            provider.GetRequiredService<IDavTransport>(),
            provider.GetRequiredService<IDavResponseHandler>()));
        services.AddSingleton<ICalDavStateStore, CalDavStateStore>();
        services.AddTransient<ICalendarCapabilityService, CalendarCapabilityService>();
        services.AddSingleton<IWhatsNewService, WhatsNewService>();
        services.AddSingleton<IRelayIntroductionService>(provider => new PushRelay.RelayIntroductionService(
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetRequiredService<IPlatformCapabilities>(),
            () => provider.GetRequiredService<IPushRelayRegistrationService>()));
    }

    /// <summary>
    /// Live notifications through WinoRelay. The platform adapters (<see cref="IPushRelayDeviceIdentityStore"/>,
    /// <see cref="IPushRelayDeviceInfo"/>, <see cref="IPushTokenProvider"/>) come from the heads and are optional:
    /// without them the registration service reports <c>UnsupportedPlatform</c> and sends nothing.
    /// </summary>
    private static void RegisterPushRelayServices(IServiceCollection services)
    {
        services.AddSingleton<IPushRelayDeviceCredentialStore>(provider =>
            provider.GetService<ISecureItemStore>() is { } secureItems
                ? new SecureItemPushRelayDeviceCredentialStore(secureItems)
                : new ProtectedFilePushRelayDeviceCredentialStore(provider.GetRequiredService<ISecretProtector>(), provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<IPushRelayTokenProvider>(provider => new PushRelayTokenProvider(
            provider.GetRequiredService<IWinoAccountApiClient>(),
            provider.GetRequiredService<IWinoAccountSessionService>(),
            provider.GetRequiredService<IPushRelayDeviceCredentialStore>(),
            // Heads with a platform attestation service (iOS, Android) register their provider; the rest attest with "none".
            provider.GetService<IDeviceAttestationProvider>() ?? new NoDeviceAttestationProvider(),
            provider.GetService<IPushRelayDeviceInfo>()));
        services.AddSingleton<IPushRelayClient>(provider => new PushRelayClient(provider.GetRequiredService<IPushRelayTokenProvider>()));
        services.AddSingleton<IPushRelayStateStore>(provider => new ConfigurationPushRelayStateStore(provider.GetRequiredService<IConfigurationService>()));
        services.AddSingleton<IPushRelayMailboxRegistrationBuilder>(provider => new ImapPushRelayMailboxRegistrationBuilder(
            provider.GetRequiredService<IAccountService>(), provider.GetService<IPushRelayDeviceInfo>()));
        services.AddSingleton<IPushRelayRegistrationService>(provider => new PushRelayRegistrationService(
            provider.GetRequiredService<IPushRelayClient>(),
            provider.GetRequiredService<IPushRelayTokenProvider>(),
            provider.GetRequiredService<IPushRelayStateStore>(),
            provider.GetRequiredService<IWinoAccountSessionService>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetServices<IPushRelayMailboxRegistrationBuilder>(),
            provider.GetRequiredService<IPlatformCapabilities>(),
            provider.GetRequiredService<IAppMetadataService>(),
            provider.GetService<ITranslationService>(),
            provider.GetService<IMessenger>(),
            provider.GetService<IPushRelayDeviceIdentityStore>(),
            provider.GetService<IPushRelayDeviceInfo>(),
            provider.GetService<IPushTokenProvider>(),
            socketKeys: provider.GetService<IPushRelaySocketKeyStore>(),
            preferences: provider.GetService<IPreferencesService>()));
        // Lazy, so the profile service (which signs the relay device out) is never built around the relay graph.
        services.AddSingleton<Func<IPushRelayRegistrationService>>(provider => () => provider.GetRequiredService<IPushRelayRegistrationService>());
        services.AddSingleton<IRelayAlertCoverage>(new RelayAlertCoverage());
        services.AddSingleton<IPushRelayNotificationHandler>(provider => new PushRelayNotificationHandler(
            provider.GetRequiredService<IPushRelayRegistrationService>(),
            provider.GetRequiredService<IAccountService>(),
            () => provider.GetRequiredService<IApplicationRuntime>(),
            () => provider.GetService<INotificationBuilder>(),
            provider.GetService<IPushRelayAppManagedRegistrationStore>(),
            provider.GetRequiredService<IRelayAlertCoverage>(),
            provider.GetService<IPushRelayDeviceInfo>()));
    }
}
