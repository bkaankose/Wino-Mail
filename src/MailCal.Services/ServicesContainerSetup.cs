using Microsoft.Extensions.DependencyInjection;
using MailCal.Core.Domain.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Services.CalDav;
using MailCal.Services.CardDav;
using MailCal.Services.Dav;

namespace MailCal.Services;

public static class ServicesContainerSetup
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AddSingleton<MailCal.Core.Domain.Models.MailItem.DraftUpdateRegistry>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IDatabaseSchemaService, DatabaseSchemaService>();
        services.AddSingleton<IMigrationClock, SystemMigrationClock>();
        services.AddSingleton<IAuthenticationTokenMigrationService, AuthenticationTokenMigrationService>();
        services.AddSingleton<IMigrationCoordinator, DatabaseMigrationCoordinator>();
        services.AddSingleton<IDatabaseService, DatabaseService>();

        services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();

        services.AddSingleton<IMailCalLogger, MailCalLogger>();
        services.AddSingleton<INotificationPolicyService, NotificationPolicyService>();
        services.AddSingleton<IActivationStateService, ActivationStateService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<IContentTypeDetectionService, ContentTypeDetectionService>();
        services.AddSingleton<IAttachmentFileService, AttachmentFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddSingleton<IActivationFileImportService, ActivationFileImportService>();
        // Needs the platform INotificationBuilder; each head starts it once its shell is running.
        services.AddSingleton<ICalendarReminderServer, CalendarReminderServer>();

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
        services.AddSingleton<ISyncSnapshotKeyService, SyncSnapshotKeyService>();
        services.AddTransient<ILocalBackupService, LocalBackupService>();
        services.AddSingleton<IPictureStorageService, PictureStorageService>();
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
    }
}
