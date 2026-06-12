using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc;
using Wino.Ipc.Contracts.Generated;
using Wino.Mail.WinUI.Services;

namespace Wino.Mail.WinUI;

public static class RemoteServicesContainerSetup
{
    /// <summary>
    /// Swaps every database/synchronization-backed service for its generated remote proxy
    /// over the background companion pipe. Must run after RegisterCoreServices and
    /// RegisterSharedServices so these registrations win. File-based and UI-local services
    /// (IMimeFileService, ITranslationService, IPreferencesService, dialogs, theming,
    /// interactive authentication) keep their in-process implementations.
    /// </summary>
    public static void RegisterRemoteServices(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundServiceConnection>();
        services.AddSingleton<IRpcClient>(provider => provider.GetRequiredService<BackgroundServiceConnection>());
        services.AddSingleton<IBackgroundServiceConnection>(provider => provider.GetRequiredService<BackgroundServiceConnection>());

        services.AddSingleton<IAccountService, AccountServiceRemoteProxy>();
        services.AddSingleton<IBackgroundServiceControl, BackgroundServiceControlRemoteProxy>();
        services.AddSingleton<ICalendarService, CalendarServiceRemoteProxy>();
        services.AddSingleton<IContactPictureFileService, ContactPictureFileServiceRemoteProxy>();
        services.AddSingleton<IContactService, ContactServiceRemoteProxy>();
        services.AddSingleton<IEmailTemplateService, EmailTemplateServiceRemoteProxy>();
        services.AddSingleton<IFolderService, FolderServiceRemoteProxy>();
        services.AddSingleton<IKeyboardShortcutService, KeyboardShortcutServiceRemoteProxy>();
        services.AddSingleton<IMailCategoryService, MailCategoryServiceRemoteProxy>();
        services.AddSingleton<IMailRenderService, MailRenderServiceRemoteProxy>();
        services.AddSingleton<IMailService, MailServiceRemoteProxy>();
        services.AddSingleton<ISentMailReceiptService, SentMailReceiptServiceRemoteProxy>();
        services.AddSingleton<ISignatureService, SignatureServiceRemoteProxy>();
        services.AddSingleton<ISmimeService, SmimeServiceRemoteProxy>();
        services.AddSingleton<ISynchronizationManager, SynchronizationManagerRemoteProxy>();
        services.AddSingleton<IThumbnailCacheService, ThumbnailCacheServiceRemoteProxy>();
        services.AddSingleton<IWinoAccountDataSyncService, WinoAccountDataSyncServiceRemoteProxy>();
        services.AddSingleton<IWinoAccountProfileService, WinoAccountProfileServiceRemoteProxy>();
        services.AddSingleton<IWinoRequestDelegator, WinoRequestDelegatorRemoteProxy>();
    }
}
