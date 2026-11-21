using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>The People app of an account (Windows AccountPeopleSettingsPage): its address books and contact categories.</summary>
public sealed class AccountPeoplePageViewController(AccountPeopleSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : AccountAppPageViewController<AccountPeopleSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var empty = new WinoSettingsCard(Translator.AccountApps_NoAddressBooks, null, WinoIconGlyph.People);
        Bind.Visible(empty, vm, nameof(vm.HasAddressBooks), s => !s.HasAddressBooks);
        var books = AddGroup(Translator.AccountApps_AddressBooksHeader, empty);
        Bind.Collection(vm.AddressBooks, () => AccountCollectionRows.Fill(books, empty, vm.AddressBooks.ToList(), WinoIconGlyph.People));

        var categories = CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand);
        Bind.Visible(categories, vm, nameof(vm.IsCategoriesVisible), s => s.IsCategoriesVisible);
        Add(categories);

    }
}
