using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Storage: the local MIME cache summary with a scan button, then one expander per account
/// (account icon, name, usage) with "delete all" and "delete older than" actions (Windows StoragePage).
/// </summary>
public sealed class StoragePageViewController(StoragePageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<StoragePageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _accounts = new();
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var summary = Bind.Label(vm, nameof(vm.SummaryText), s => s.SummaryText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var scan = Bind.Button(Translator.SettingsStorage_ScanFolder, vm.RefreshStorageCommand, primary: true);
        Bind.Enabled(scan, vm, nameof(vm.IsBusy), s => !s.IsBusy);
        var hero = Card(Translator.SettingsStorage_Title, Translator.SettingsStorage_Description, WinoIconGlyph.Storage, scan);
        hero.BottomContent = summary;
        AddGroup(null, hero);

        Add(_accounts);
        Bind.Collection(vm.AccountStorageItems, RebuildAccounts);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);

    private void RebuildAccounts()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _accounts.Clear();

        foreach (var item in ViewModel.AccountStorageItems.ToList())
        {
            var expander = new WinoSettingsExpander(item.AccountName, item.SizeDescription);
            var icon = new WinoAccountIconView(28) { Account = MailAccountIconInfoFactory.Create(item.Account, pictures) };
            expander.HeaderCard.LeadingView = icon;
            rows.Bind(item, nameof(item.SizeDescription), i => i.SizeDescription, text => expander.HeaderCard.Description = text);

            var deleteAll = rows.Button(Translator.SettingsStorage_DeleteAll_Button, item.DeleteAllCommand, () => item);
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.SettingsStorage_DeleteAll_Title, Translator.SettingsStorage_DeleteAll_Description, WinoIconGlyph.None, deleteAll),
                item, nameof(item.IsBusy), i => !i.IsBusy));

            var older = Row(
                rows.Button(Translator.SettingsStorage_DeleteOld_1Month, item.DeleteOneMonthCommand, () => item),
                rows.Button(Translator.SettingsStorage_DeleteOld_3Months, item.DeleteThreeMonthsCommand, () => item),
                rows.Button(Translator.SettingsStorage_DeleteOld_6Months, item.DeleteSixMonthsCommand, () => item),
                rows.Button(Translator.SettingsStorage_DeleteOld_1Year, item.DeleteYearCommand, () => item));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.SettingsStorage_DeleteOld_Title, Translator.SettingsStorage_DeleteOld_Description, WinoIconGlyph.None, older),
                item, nameof(item.IsBusy), i => !i.IsBusy));
            _accounts.Add(expander);
        }
        _accounts.Hidden = _accounts.RowCount == 0;
    }
}
