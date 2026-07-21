using Microsoft.Extensions.DependencyInjection;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Mail.Uwp.Services;

/// <summary>
/// Registers split UI services. Database reads use a dedicated read-only SQLite
/// connection in UWP; mutations, authentication and synchronization use AppService.
/// </summary>
internal static class RemoteServicesContainerSetup
{
    public static IServiceCollection AddRemoteBackendServices(
        this IServiceCollection services,
        CompanionConnectionService connection)
    {
        services.AddSingleton(connection);
        services.AddSingleton<IWinoRpcClient>(connection);

        services.AddSingleton<IDatabaseService>(provider =>
            new DatabaseService(
                provider.GetRequiredService<IApplicationConfiguration>(),
                DatabaseAccessMode.ReadOnly));
        services.AddSingleton<IAuthenticationProvider, LocalReadOnlyAuthenticationProvider>();

        services.AddSingleton<AccountService>();
        services.AddSingleton<AccountServiceRemoteProxy>();
        services.AddSingleton<IAccountService>(provider => new AccountServiceHybridProxy(
            provider.GetRequiredService<AccountService>(),
            provider.GetRequiredService<AccountServiceRemoteProxy>()));

        services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryServiceRemoteProxy>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileServiceRemoteProxy>();
        services.AddSingleton<ICalDavClient, CalDavClientRemoteProxy>();
        services.AddSingleton<CalendarService>();
        services.AddSingleton<CalendarServiceRemoteProxy>();
        services.AddSingleton<ICalendarService>(provider => new CalendarServiceHybridProxy(
            provider.GetRequiredService<CalendarService>(),
            provider.GetRequiredService<CalendarServiceRemoteProxy>()));

        services.AddSingleton<ICompanionBackendControl, CompanionBackendControlRemoteProxy>();
        services.AddSingleton<ContactPictureFileService>();
        services.AddSingleton<ContactPictureFileServiceRemoteProxy>();
        services.AddSingleton<IContactPictureFileService>(provider => new ContactPictureFileServiceHybridProxy(
            provider.GetRequiredService<ContactPictureFileService>(),
            provider.GetRequiredService<ContactPictureFileServiceRemoteProxy>()));

        services.AddSingleton<ContactService>();
        services.AddSingleton<ContactServiceRemoteProxy>();
        services.AddSingleton<IContactService>(provider => new ContactServiceHybridProxy(
            provider.GetRequiredService<ContactService>(),
            provider.GetRequiredService<ContactServiceRemoteProxy>()));

        services.AddSingleton<EmailTemplateService>();
        services.AddSingleton<EmailTemplateServiceRemoteProxy>();
        services.AddSingleton<IEmailTemplateService>(provider => new EmailTemplateServiceHybridProxy(
            provider.GetRequiredService<EmailTemplateService>(),
            provider.GetRequiredService<EmailTemplateServiceRemoteProxy>()));

        services.AddSingleton<FolderService>();
        services.AddSingleton<FolderServiceRemoteProxy>();
        services.AddSingleton<IFolderService>(provider => new FolderServiceHybridProxy(
            provider.GetRequiredService<FolderService>(),
            provider.GetRequiredService<FolderServiceRemoteProxy>()));

        services.AddSingleton<IImapTestService, ImapTestServiceRemoteProxy>();
        services.AddSingleton<KeyboardShortcutService>();
        services.AddSingleton<KeyboardShortcutServiceRemoteProxy>();
        services.AddSingleton<IKeyboardShortcutService>(provider => new KeyboardShortcutServiceHybridProxy(
            provider.GetRequiredService<KeyboardShortcutService>(),
            provider.GetRequiredService<KeyboardShortcutServiceRemoteProxy>()));

        services.AddSingleton<MailCategoryService>();
        services.AddSingleton<MailCategoryServiceRemoteProxy>();
        services.AddSingleton<IMailCategoryService>(provider => new MailCategoryServiceHybridProxy(
            provider.GetRequiredService<MailCategoryService>(),
            provider.GetRequiredService<MailCategoryServiceRemoteProxy>()));

        services.AddSingleton<MailService>();
        services.AddSingleton<MailServiceRemoteProxy>();
        services.AddSingleton<IMailService>(provider => new MailServiceHybridProxy(
            provider.GetRequiredService<MailService>(),
            provider.GetRequiredService<MailServiceRemoteProxy>()));

        services.AddSingleton<IMimeStorageService, MimeStorageServiceRemoteProxy>();
        services.AddSingleton<SentMailReceiptService>();
        services.AddSingleton<SentMailReceiptServiceRemoteProxy>();
        services.AddSingleton<ISentMailReceiptService>(provider => new SentMailReceiptServiceHybridProxy(
            provider.GetRequiredService<SentMailReceiptService>(),
            provider.GetRequiredService<SentMailReceiptServiceRemoteProxy>()));

        services.AddSingleton<SignatureService>();
        services.AddSingleton<SignatureServiceRemoteProxy>();
        services.AddSingleton<ISignatureService>(provider => new SignatureServiceHybridProxy(
            provider.GetRequiredService<SignatureService>(),
            provider.GetRequiredService<SignatureServiceRemoteProxy>()));

        services.AddSingleton<ISynchronizationManager, SynchronizationManagerRemoteProxy>();
        services.AddSingleton<IUnsubscriptionService, UnsubscriptionServiceRemoteProxy>();
        services.AddSingleton<IWinoAccountDataSyncService, WinoAccountDataSyncServiceRemoteProxy>();
        services.AddSingleton<WinoAccountProfileServiceRemoteProxy>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileHybridService>();
        services.AddSingleton<IWinoRequestDelegator, WinoRequestDelegatorRemoteProxy>();

        return services;
    }
}
