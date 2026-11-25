using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>
/// Management: Wino Account backups live on the Backup and restore page next to the backup file; account deletion opens
/// the website's deletion page (Windows WinoAccountBackupRestoreCard, WinoAccountDeleteAccountCard).
/// </summary>
internal sealed partial class WinoAccountPage
{
    private IEnumerable<Gtk.Widget> ManagementRows()
    {
        var vm = ViewModel;
        yield return Settings.Command(Translator.SettingsBackupRestore_Title, Translator.WinoAccount_Management_BackupRestoreDescription_Device,
            WinoIconGlyph.Sync, vm.OpenBackupRestoreCommand);
        var delete = Settings.Command(Translator.WinoAccount_Management_DeleteAccount_Title, Translator.WinoAccount_Management_DeleteAccount_Description,
            WinoIconGlyph.Delete, vm.OpenAccountDeletionCommand, external: true);
        delete.AddCssClass("error");
        yield return delete;
    }
}
