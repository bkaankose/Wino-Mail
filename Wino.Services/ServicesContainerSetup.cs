using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public static class ServicesContainerSetup
{
    /// <summary>
    /// Companion-process services: everything from RegisterSharedServices plus the
    /// database/MimeKit/Ical.Net-backed half that must never load in the UI process.
    /// </summary>
    public static void RegisterCompanionServices(this IServiceCollection services)
    {
        services.RegisterSharedServices();

        services.AddSingleton<IDatabaseService, DatabaseService>();

        // Full MIME file store replaces the shared MimeKit-free registration.
        services.AddSingleton<MimeFileServiceInternal>();
        services.AddSingleton<IMimeFileService>(provider => provider.GetRequiredService<MimeFileServiceInternal>());
        services.AddSingleton<IMimeFileServiceInternal>(provider => provider.GetRequiredService<MimeFileServiceInternal>());

        services.AddTransient<ICalendarService, CalendarService>();
        services.AddTransient<IMailService, MailService>();
        services.AddTransient<IMailServiceInternal, MailService>();
        services.AddTransient<IMailCategoryService, MailCategoryService>();
        services.AddTransient<ISentMailReceiptService, SentMailReceiptService>();
        services.AddTransient<ISentMailReceiptServiceInternal, SentMailReceiptService>();
        services.AddTransient<IFolderService, FolderService>();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IContactService, ContactService>();
        services.AddTransient<IContactServiceInternal, ContactService>();
        services.AddSingleton<IWinoAccountApiClient, WinoAccountApiClient>();
        services.AddTransient<ISignatureService, SignatureService>();
        services.AddTransient<IEmailTemplateService, EmailTemplateService>();
        services.AddTransient<IKeyboardShortcutService, KeyboardShortcutService>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileService>();
        services.AddTransient<IWinoAccountDataSyncService, WinoAccountDataSyncService>();
        services.AddSingleton<IContactPictureFileService, ContactPictureFileService>();
        services.AddTransient<IThumbnailCacheService, ThumbnailCacheService>();

        services.AddTransient<ICalDavClient, CalDavClient>();
    }
}
