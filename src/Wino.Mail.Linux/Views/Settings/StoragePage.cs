using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>Storage (Windows StoragePage): the scan card with its summary, then per-account size and clean-up.</summary>
internal sealed class StoragePage(StoragePageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<StoragePageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _accounts;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var scan = WinoSettingsRows.Action(Translator.SettingsStorage_Title, Translator.SettingsStorage_Description, WinoIconGlyph.Storage);
        var spinner = Settings.Visible(WinoSettingsRows.Suffix(scan, Adw.Spinner.New()), vm, nameof(vm.IsBusy), static s => s.IsBusy);
        spinner.SetSizeRequest(16, 16);
        var button = Settings.Button(Translator.SettingsStorage_ScanFolder, vm.RefreshStorageCommand, suggested: true);
        // The command binding drives sensitivity from CanExecute; the scan also has to wait while a clean-up runs.
        var busyHost = WinoSettingsRows.Suffix(scan, Gtk.Box.New(Gtk.Orientation.Horizontal, 0));
        busyHost.Append(button);
        Settings.Enabled(busyHost, vm, nameof(vm.IsBusy), static s => !s.IsBusy);

        var summary = WinoSettingsRows.Caption(null);
        var summaryRow = WinoSettingsRows.Custom(summary);
        Settings.Bind(vm, nameof(vm.SummaryText), static s => s.SummaryText, text =>
        {
            summary.SetText(text ?? string.Empty);
            summaryRow.SetVisible(!string.IsNullOrEmpty(text));
        });
        AddGroup(null, scan, summaryRow);

        _accounts = Page.AddGroup();
        Settings.Collection(vm.AccountStorageItems, RebuildAccounts);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);

    private void RebuildAccounts()
    {
        if (_accounts is null) return;
        _rows?.Scope.Dispose();
        _accounts.Clear();
        var rows = _rows = Settings.Child();
        foreach (var item in ViewModel.AccountStorageItems.ToList())
        {
            static bool Idle(AccountStorageItemViewModel i) => !i.IsBusy;

            var deleteAll = WinoSettingsRows.Action(Translator.SettingsStorage_DeleteAll_Title, Translator.SettingsStorage_DeleteAll_Description);
            WinoSettingsRows.Suffix(deleteAll, rows.Button(Translator.SettingsStorage_DeleteAll_Button, item.DeleteAllCommand, () => item, destructive: true));
            rows.Enabled(deleteAll, item, nameof(item.IsBusy), Idle);

            // Four buttons beside the text do not fit a narrow window, so they wrap in a row of their own.
            var olderTitle = rows.Enabled(WinoSettingsRows.Action(Translator.SettingsStorage_DeleteOld_Title, Translator.SettingsStorage_DeleteOld_Description),
                item, nameof(item.IsBusy), Idle);
            var buttons = Adw.WrapBox.New();
            buttons.SetChildSpacing(8);
            buttons.SetLineSpacing(8);
            buttons.Append(rows.Button(Translator.SettingsStorage_DeleteOld_1Month, item.DeleteOneMonthCommand, () => item));
            buttons.Append(rows.Button(Translator.SettingsStorage_DeleteOld_3Months, item.DeleteThreeMonthsCommand, () => item));
            buttons.Append(rows.Button(Translator.SettingsStorage_DeleteOld_6Months, item.DeleteSixMonthsCommand, () => item));
            buttons.Append(rows.Button(Translator.SettingsStorage_DeleteOld_1Year, item.DeleteYearCommand, () => item));
            var older = rows.Enabled(WinoSettingsRows.Custom(buttons, Translator.SettingsStorage_DeleteOld_Title), item, nameof(item.IsBusy), Idle);

            var name = item.AccountName ?? string.Empty;
            var expander = WinoSettingsRows.Expander(name, item.SizeDescription, WinoIconGlyph.None, false, deleteAll, olderTitle, older);
            var icon = new WinoAccountIcon(28) { Account = MailAccountIconInfoFactory.Create(item.Account, pictures), AccessibleName = name };
            expander.AddPrefix(icon.Root);
            rows.Subtitle(expander, item, nameof(item.SizeDescription), static i => i.SizeDescription);
            _accounts.Add(expander);
        }
    }
}
