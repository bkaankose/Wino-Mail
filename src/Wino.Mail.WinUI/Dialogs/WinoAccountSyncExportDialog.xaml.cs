namespace Wino.Dialogs;

/// <summary>
/// Confirms a backup to the Wino Account. The caller asks for the backup password and runs the
/// export after the dialog closes, because a second dialog can't open while this one is shown.
/// </summary>
public sealed partial class WinoAccountSyncExportDialog : Microsoft.UI.Xaml.Controls.ContentDialog
{
    public WinoAccountSyncExportDialog()
    {
        InitializeComponent();
    }
}
