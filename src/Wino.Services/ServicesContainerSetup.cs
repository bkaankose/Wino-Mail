using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.AI.ContentProcessing;
using Wino.Services.CardDav;
using Wino.Services.Dav;

namespace Wino.Services;

public static class ServicesContainerSetup
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
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
        services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
        services.AddSingleton<IShareActivationService, ShareActivationService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddSingleton<IActivationFileImportService, ActivationFileImportService>();
        services.AddTransient<IMimeStorageService, MimeStorageService>();

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
        services.AddTransient<ITaskService, TaskService>();
        services.AddTransient<ITaskQueryService>(provider => provider.GetRequiredService<ITaskService>());
        services.AddTransient<ISignatureService, SignatureService>();
        services.AddTransient<IEmailTemplateService, EmailTemplateService>();
        services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
        services.AddTransient<ICalendarContextMenuItemService, CalendarContextMenuItemService>();
        services.AddSingleton<IKnownImapProviderCatalogLoader, KnownImapProviderCatalogLoader>();
        services.AddSingleton<IKnownImapProviderCatalog, EmbeddedKnownImapProviderCatalog>();
        services.AddTransient<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddSingleton<IKeyboardShortcutService, KeyboardShortcutService>();
        services.AddSingleton<IWinoAccountApiClient, WinoAccountApiClient>();
        services.AddSingleton<IIntelligenceBackend, CloudIntelligenceBackend>();
        services.AddSingleton<IIntelligenceSearchEligibilityService, IntelligenceSearchEligibilityService>();
        services.AddSingleton<IIntelligenceSearchService, IntelligenceSearchService>();
        services.AddSingleton<ILocalIntelligenceSearchEngine, LocalIntelligenceSearchEngine>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileService>();
        services.AddSingleton<IWinoBillingService, WinoBillingService>();
        services.AddSingleton<IWinoAccountIntelligenceSnapshotService, WinoAccountIntelligenceSnapshotService>();
        services.AddSingleton<ISemanticIndexJobRegistry, SemanticIndexJobRegistry>();
        services.AddSingleton<IIntelligenceMessageContextResolver, IntelligenceMessageContextResolver>();
        services.AddSingleton<ISemanticIndexCoordinator, SemanticIndexCoordinator>();
        services.AddSingleton<IWinoIntelligenceCoordinator, WinoIntelligenceCoordinator>();
        services.AddSingleton<IIntelligenceCoverageHandoff, IntelligenceCoverageHandoff>();
        services.AddSingleton<ILocalIntelligenceStore, LocalIntelligenceStore>();
        services.AddSingleton<ILocalIntelligenceService, LocalIntelligenceService>();
        services.AddSingleton<IContentEnvelopeEncryptor>(_ =>
            new PemContentEnvelopeEncryptor(EmbeddedIntelligencePublicKeyProvider.Load()));
        services.AddTransient<IWinoAccountDataSyncService, WinoAccountDataSyncService>();
        services.AddSingleton<IContactPictureFileService, ContactPictureFileService>();
        services.AddSingleton<IAccountProfilePictureFileService, AccountProfilePictureFileService>();
        services.AddSingleton<AccountProfilePictureMigrationService>();
        services.AddSingleton<AccountProfilePictureBackfillService>();

        services.AddSingleton<IDavTransport>(_ => new DavTransport());
        services.AddSingleton<IDavMultistatusReader, DavMultistatusReader>();
        services.AddSingleton<IDavResponseHandler, DavResponseHandler>();
        services.AddSingleton<IDavCredentialStore, DavCredentialStore>();
        services.AddSingleton<IVCardCodec, VCardCodec>();
        services.AddSingleton<ICardDavPayloadStore, CardDavPayloadStore>();
        services.AddTransient<ICardDavSynchronizationStore, CardDavSynchronizationStore>();
        services.AddTransient<ICardDavClient, CardDavClient>();
        services.AddTransient<ICalDavClient>(provider => new CalDavClient(
            provider.GetRequiredService<IDavTransport>(),
            provider.GetRequiredService<IDavResponseHandler>()));
        services.AddSingleton<IUpdateManager, UpdateManager>();
    }
}
