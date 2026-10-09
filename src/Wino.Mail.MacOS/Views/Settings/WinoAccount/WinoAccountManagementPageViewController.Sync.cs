using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Sync and backup: Wino Account backups live on the Backup and restore page, next to the backup
/// file, so this section is one navigation card (Windows WinoAccountBackupRestoreCard). Export and
/// import there ask for the backup password through the sync secret sheet.
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
    }
}
