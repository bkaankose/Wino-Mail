using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.Accounts;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// Account routes: Manage accounts and its sub-pages (iOS SettingsPageRoutes, the Mac MacPageRegistry.Settings*.cs).
/// Each page activates through its ViewModel's awaitable initialization, as the iOS InitializeAsync overrides do.
/// </summary>
public static partial class SettingsPageRoutes
{
    static partial void RegisterAccountPages(AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;
        registry.Register<ManageAccountsPageFragment, AccountManagementViewModel>(WinoPage.ManageAccountsPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<ManageAccountsPageFragment, AccountManagementViewModel>(WinoPage.AccountManagementPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<AccountDetailsPageFragment, AccountDetailsPageViewModel>(WinoPage.AccountDetailsPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<MergedAccountDetailsPageFragment, MergedAccountDetailsPageViewModel>(WinoPage.MergedAccountDetailsPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<AliasManagementPageFragment, AliasManagementPageViewModel>(WinoPage.AliasManagementPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<MailCategoryManagementPageFragment, MailCategoryManagementPageViewModel>(WinoPage.MailCategoryManagementPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<SignatureManagementPageFragment, SignatureManagementPageViewModel>(WinoPage.SignatureManagementPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter),
            static async vm =>
            {
                vm.OnNavigatedFrom(NavigationMode.New, null);
                await vm.DrainPreferenceWritesAsync();
            });
        registry.Register<FolderCustomizationPageFragment, FolderCustomizationPageViewModel>(WinoPage.FolderCustomizationPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<MailFiltersPageFragment, MailFiltersPageViewModel>(WinoPage.MailFiltersPage, host, MailFiltersPageFragment.InitializeAsync);
        registry.Register<MailFilterEditorPageFragment, MailFilterEditorPageViewModel>(WinoPage.MailFilterEditorPage, host, MailFilterEditorPageFragment.InitializeAsync);
    }
}
