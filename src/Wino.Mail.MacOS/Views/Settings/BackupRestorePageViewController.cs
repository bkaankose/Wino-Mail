using System.Windows.Input;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Backup and restore (Windows BackupRestorePage): a Back up and a Restore expander, each with a
/// file row and a Wino Account row (disabled until a Wino Account is signed in), and a polite
/// progress row while a transfer runs. Passwords are asked for by the sync secret sheet.
/// </summary>
public sealed class BackupRestorePageViewController(BackupRestorePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<BackupRestorePageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var backup = Expander(Translator.SettingsBackupRestore_BackupHeader, Translator.SettingsBackupRestore_BackupDescription, WinoIconGlyph.Save, null,
            Card(Translator.SettingsBackupRestore_FileTarget, Translator.SettingsBackupRestore_BackupFileDescription, WinoIconGlyph.None,
                Bind.Button(Translator.WinoAccount_Management_LocalDataExportAction, vm.ExportLocalDataCommand)),
            WinoAccountRow(Translator.SettingsBackupRestore_BackupWinoAccountDescription, Translator.WinoAccount_Management_LocalDataExportAction, vm.ExportToWinoAccountCommand));
        backup.IsExpanded = true;
        Add(backup);

        var restore = Expander(Translator.SettingsBackupRestore_RestoreHeader, Translator.SettingsBackupRestore_RestoreDescription, WinoIconGlyph.ArrowDownload, null,
            Card(Translator.SettingsBackupRestore_FileTarget, Translator.SettingsBackupRestore_RestoreFileDescription, WinoIconGlyph.None,
                Bind.Button(Translator.WinoAccount_Management_LocalDataImportAction, vm.ImportLocalDataCommand)),
            WinoAccountRow(Translator.SettingsBackupRestore_RestoreWinoAccountDescription, Translator.WinoAccount_Management_LocalDataImportAction, vm.ImportFromWinoAccountCommand));
        restore.IsExpanded = true;
        Add(restore);

        // Windows: a spinner and "Working on your backup..." under the expanders, announced politely.
        var spinner = IntelligenceViews.Spinner();
        var text = WinoStyle.Label(Translator.SettingsBackupRestore_InProgress, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var progress = Row(spinner, text);
        progress.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.IsDataTransferInProgress), s => s.IsDataTransferInProgress, on =>
        {
            var wasHidden = progress.Hidden;
            progress.Hidden = !on;
            IntelligenceViews.SetSpinning(spinner, on);
            if (on && wasHidden)
                NSAccessibility.PostNotification(View.Window ?? (NSObject)View, new Foundation.NSString("AXAnnouncementRequested"),
                    Foundation.NSDictionary.FromObjectAndKey(new Foundation.NSString(Translator.SettingsBackupRestore_InProgress), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
        });
        Add(progress);
    }

    /// <summary>
    /// The Wino Account row dims while signed out (Windows IsEnabled binding). The card toggles its
    /// trailing button, so the button is set back from CanExecute afterwards (a transfer may be running).
    /// </summary>
    private WinoSettingsCard WinoAccountRow(string description, string action, ICommand command)
    {
        var vm = ViewModel;
        var button = Bind.Button(action, command);
        var card = Card(Translator.SettingsBackupRestore_WinoAccountTarget, description, WinoIconGlyph.None, button);
        Bind.Bind(vm, nameof(vm.IsWinoAccountSignedIn), s => s.IsWinoAccountSignedIn, signedIn =>
        {
            card.IsEnabled = signedIn;
            button.Enabled = command.CanExecute(null);
        });
        return card;
    }
}
