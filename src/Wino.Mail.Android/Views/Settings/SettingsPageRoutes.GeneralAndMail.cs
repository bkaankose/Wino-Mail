using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.GeneralAndMail;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings;

public static partial class SettingsPageRoutes
{
    /// <summary>
    /// General and Mail pages (iOS SettingsPageRoutes, same order). Activation mirrors each iOS controller's
    /// InitializeAsync / DeactivateAsync override; pages without one keep OnNavigatedTo / OnNavigatedFrom.
    /// </summary>
    static partial void RegisterGeneralAndMailPages(AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;

        // OnNavigatedTo / OnNavigatedFrom; the fragment owns the 30 s snooze refresh while its view exists.
        registry.Register<NotificationSettingsPageFragment, NotificationSettingsPageViewModel>(WinoPage.NotificationSettingsPage, host);
        registry.Register<AboutPageFragment, AboutPageViewModel>(WinoPage.AboutPage, host);
        registry.Register<MailPreferencesPageFragment, MailPreferencesPageViewModel>(WinoPage.MailPreferencesPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
        registry.Register<MessageListPageFragment, MessageListPageViewModel>(WinoPage.MessageListPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
        registry.Register<UnreadBadgeSettingsPageFragment, UnreadBadgeSettingsPageViewModel>(WinoPage.UnreadBadgeSettingsPage, host);
        registry.Register<AccountUnreadBadgePageFragment, AccountUnreadBadgePageViewModel>(WinoPage.AccountUnreadBadgePage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter),
            static async vm =>
            {
                vm.OnNavigatedFrom(NavigationMode.New, null);
                await vm.DrainPreferenceWritesAsync();
            });
        registry.Register<ReadComposePanePageFragment, ReadComposePanePageViewModel>(WinoPage.ReadComposePanePage, host);
        // iOS InitializeAsync: OnNavigatedTo, then the load the page waits for (the fragment reads the result from the entry).
        registry.Register<EmailTemplatesPageFragment, EmailTemplatesPageViewModel>(WinoPage.EmailTemplatesPage, host,
            activated: EmailTemplatesPageFragment.LoadAsync);
        registry.Register<CreateEmailTemplatePageFragment, CreateEmailTemplatePageViewModel>(WinoPage.CreateEmailTemplatePage, host,
            activated: CreateEmailTemplatePageFragment.LoadAsync);
        registry.Register<StoragePageFragment, StoragePageViewModel>(WinoPage.StoragePage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
    }
}
