using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>The People app of an account (Windows AccountPeopleSettingsPage, macOS AccountPeoplePageViewController): its address books and contact categories.</summary>
internal sealed class AccountPeopleSettingsPage(AccountPeopleSettingsPageViewModel viewModel, WinoViewServices services)
    : AccountAppPage<AccountPeopleSettingsPageViewModel>(viewModel, services)
{
    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var empty = WinoSettingsRows.Action(Translator.AccountApps_NoAddressBooks, null, WinoIconGlyph.People);
        Settings.Visible(empty, vm, nameof(vm.HasAddressBooks), static s => !s.HasAddressBooks);
        var books = Page.AddGroup(Translator.AccountApps_AddressBooksHeader, null, empty);
        Settings.Collection(vm.AddressBooks, () =>
        {
            AccountCollectionRows.Fill(books, empty, [.. vm.AddressBooks], WinoIconGlyph.People);
            empty.SetVisible(!vm.HasAddressBooks);
        });

        var categories = Settings.Command(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand);
        var group = Page.AddGroup(null, null, categories);
        Settings.Visible(group.Root, vm, nameof(vm.Account), static s => s.IsCategoriesVisible);
    }

#if DEBUG
    internal override string DebugDescribeContent()
        => "books=" + string.Join(" ; ", ViewModel.AddressBooks.Select(static item => $"{item.Title}|{item.Detail}")) + $" categories={ViewModel.IsCategoriesVisible}\n";
#endif
}
