using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public static class ServicesContainerSetup
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IDatabaseService, DatabaseService>();

        services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
        services.AddSingleton<IWinoLogger, WinoLogger>();
        services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
        services.AddSingleton<IShareActivationService, ShareActivationService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddTransient<IMimeStorageService, MimeStorageService>();

        services.AddTransient<ICalendarService, CalendarService>();
        services.AddTransient<IMailService, MailService>();
        services.AddTransient<IMailCategoryService, MailCategoryService>();
        services.AddTransient<ISentMailReceiptService, SentMailReceiptService>();
        services.AddTransient<IFolderService, FolderService>();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IContactService, ContactService>();
        services.AddTransient<ISignatureService, SignatureService>();
        services.AddTransient<IEmailTemplateService, EmailTemplateService>();
        services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
        services.AddTransient<ICalendarContextMenuItemService, CalendarContextMenuItemService>();
        services.AddTransient<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddTransient<IKeyboardShortcutService, KeyboardShortcutService>();
        services.AddSingleton<IWinoAccountApiClient, WinoAccountApiClient>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileService>();
        services.AddTransient<IWinoAccountDataSyncService, WinoAccountDataSyncService>();
        services.AddSingleton<IContactPictureFileService, ContactPictureFileService>();

        services.AddTransient<ICalDavClient, CalDavClient>();
        services.AddSingleton<IUpdateManager, UpdateManager>();
    }
}
