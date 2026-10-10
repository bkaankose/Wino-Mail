using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Management: Wino Account backups live on the Backup and restore page, next to the backup file
/// (Windows WinoAccountBackupRestoreCard); export and import there ask for the backup password through
/// the sync secret sheet. Account deletion opens the website's deletion page (WinoAccountDeleteAccountCard).
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private IEnumerable<NSView> SyncCards()
    {
        var vm = ViewModel;
        var card = CommandCard(Translator.SettingsBackupRestore_Title, Translator.WinoAccount_Management_BackupRestoreDescription_Device, WinoIconGlyph.None, vm.OpenBackupRestoreCommand);
        card.LeadingView = new WinoIconView(WinoIconGlyph.Sync, 20, WinoStyle.Informational);
        card.AccessibilityIdentifier = "WinoAccountBackupRestoreCard";
        yield return card;

        var delete = CommandCard(Translator.WinoAccount_Management_DeleteAccount_Title, Translator.WinoAccount_Management_DeleteAccount_Description, WinoIconGlyph.None, vm.OpenAccountDeletionCommand);
        delete.LeadingView = new WinoIconView(WinoIconGlyph.Delete, 20, WinoStyle.Critical);
        delete.SetActionIcon(WinoIconGlyph.Open);
        delete.AccessibilityIdentifier = "WinoAccountDeleteAccountCard";
        yield return delete;
    }
}
