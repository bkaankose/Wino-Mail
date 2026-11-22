using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Backup and restore (Windows BackupRestorePage): the two always-open expanders become two groups, each with a file on
/// this device and the Wino Account. The Wino Account rows stay insensitive while no Wino Account is signed in.
/// </summary>
internal sealed class BackupRestorePage(BackupRestorePageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<BackupRestorePageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var backup = Page.AddGroup(Translator.SettingsBackupRestore_BackupHeader, Translator.SettingsBackupRestore_BackupDescription);
        backup.AddRange(
            Row(Translator.SettingsBackupRestore_FileTarget_Device, Translator.SettingsBackupRestore_BackupFileDescription_Device, WinoIconGlyph.Save,
                Translator.WinoAccount_Management_LocalDataExportAction, vm.ExportLocalDataCommand, account: false),
            Row(Translator.SettingsBackupRestore_WinoAccountTarget, Translator.SettingsBackupRestore_BackupWinoAccountDescription, WinoIconGlyph.Person,
                Translator.WinoAccount_Management_LocalDataExportAction, vm.ExportToWinoAccountCommand, account: true));

        var restore = Page.AddGroup(Translator.SettingsBackupRestore_RestoreHeader, Translator.SettingsBackupRestore_RestoreDescription);
        restore.AddRange(
            Row(Translator.SettingsBackupRestore_FileTarget_Device, Translator.SettingsBackupRestore_RestoreFileDescription, WinoIconGlyph.ArrowDownload,
                Translator.WinoAccount_Management_LocalDataImportAction, vm.ImportLocalDataCommand, account: false),
            Row(Translator.SettingsBackupRestore_WinoAccountTarget, Translator.SettingsBackupRestore_RestoreWinoAccountDescription, WinoIconGlyph.Person,
                Translator.WinoAccount_Management_LocalDataImportAction, vm.ImportFromWinoAccountCommand, account: true));

        var progress = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        progress.Append(Adw.Spinner.New());
        progress.Append(Gtk.Label.New(Translator.SettingsBackupRestore_InProgress));
        var busy = AddGroup(null, WinoSettingsRows.Custom(progress, Translator.SettingsBackupRestore_InProgress));
        Settings.Visible(busy.Root, vm, nameof(vm.IsDataTransferInProgress), static s => s.IsDataTransferInProgress);
    }

    private Adw.ActionRow Row(string title, string description, WinoIconGlyph glyph, string action, System.Windows.Input.ICommand command, bool account)
    {
        var row = WinoSettingsRows.Action(title, description, glyph);
        var button = WinoSettingsRows.Suffix(row, Settings.Button(action, command));
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(button, $"{action}, {title}");
        if (account) Settings.Enabled(row, ViewModel, nameof(ViewModel.IsWinoAccountSignedIn), static s => s.IsWinoAccountSignedIn);
        return row;
    }
}
