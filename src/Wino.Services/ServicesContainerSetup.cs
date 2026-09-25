using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.AI.ContentProcessing;
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
        services.AddSingleton<IWindowsAttachmentPolicyService, WindowsAttachmentPolicyService>();
        services.AddSingleton<IAttachmentFileService, AttachmentFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddSingleton<IActivationFileImportService, ActivationFileImportService>();

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
        services.AddSingleton<IWinoAccountSessionService>(provider => WinoAccountSessionService.For(provider.GetRequiredService<IDatabaseService>()));
        services.AddSingleton<IWinoAccountApiClient, WinoAccountApiClient>();
        services.AddSingleton<IIntelligenceBackend, CloudIntelligenceBackend>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileService>();
        services.AddSingleton<IWinoBillingService, WinoBillingService>();
        services.AddSingleton<IWinoPendingCheckoutStore, WinoPendingCheckoutStore>();
        services.AddSingleton<IWinoPurchaseReconciliationService, WinoPurchaseReconciliationService>();
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
        services.AddSingleton<IIntelligenceKeyProtector, DpapiIntelligenceKeyProtector>();
        services.AddSingleton<IntelligenceResultKeyPresence>();
        services.AddSingleton<IIntelligenceResultKeyStore, IntelligenceResultKeyStore>();
        services.AddSingleton<IntelligenceResultKeyLifecycle>();
        services.AddSingleton<IntelligenceTransportKeyProvider>();
        services.AddSingleton<MailIntelligenceResultPageReader>();
        services.AddSingleton<ILocalIntelligenceService, LocalIntelligenceService>();
        services.AddSingleton<IContentEnvelopeEncryptor>(_ =>
            new PemContentEnvelopeEncryptor(EmbeddedIntelligencePublicKeyProvider.Load()));
        services.AddTransient<IWinoAccountDataSyncService, WinoAccountDataSyncService>();
        services.AddSingleton<IPictureStorageService, PictureStorageService>();
        services.AddSingleton<AccountProfilePictureMaintenance>();

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
        services.AddSingleton<IWhatsNewService, WhatsNewService>();
    }
}
