using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public static class ServicesContainerSetup
{
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationService, TranslationService>();
        
        // Register DatabaseService as both IDatabaseService and IDbContextFactory
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<IDatabaseService>(sp => sp.GetRequiredService<DatabaseService>());
        services.AddSingleton<IDbContextFactory<WinoDbContext>>(sp => sp.GetRequiredService<DatabaseService>());

        services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
        services.AddSingleton<IWinoLogger, WinoLogger>();
        services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();

        services.AddTransient<ICalendarService, CalendarService>();
        services.AddTransient<IMailService, MailService>();
        services.AddTransient<IFolderService, FolderService>();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IContactService, ContactService>();
        services.AddTransient<ISignatureService, SignatureService>();
        services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
        services.AddTransient<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddTransient<IKeyboardShortcutService, KeyboardShortcutService>();
    }
}
