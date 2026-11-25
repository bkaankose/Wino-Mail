using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// Troubleshooting of an account (Windows AccountTroubleshootingPage, macOS AccountTroubleshootingPageViewController): the
/// "no logs yet" note, the protocol log switch, and export and upload of the logs. The navigation parameter is the account id.
/// </summary>
internal sealed class AccountTroubleshootingPage(AccountTroubleshootingPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<AccountTroubleshootingPageViewModel>(viewModel, services)
{
#if DEBUG
    internal static AccountTroubleshootingPage? DebugCurrent { get; private set; }

    internal AccountTroubleshootingPageViewModel DebugViewModel => ViewModel;

    internal string DebugDescribe()
        => $"page=AccountTroubleshooting account={ViewModel.Account?.Address} log={ViewModel.IsProtocolLogEnabled} hasLogs={ViewModel.HasLogs} note={ViewModel.IsNoLogsNoteVisible}"
           + $" export={ViewModel.ExportProtocolLogsCommand.CanExecute(null)} upload={ViewModel.UploadProtocolLogsCommand.CanExecute(null)}\n";
#endif

    protected override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        DebugCurrent = this;
        Settings.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
        }));
#endif
        var noLogs = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.AccountTroubleshooting_NoLogsMessage) { IsClosable = false };
        Settings.Own(noLogs);
        var bars = Page.AddGroup();
        bars.HidesWhenEmpty = false;
        bars.Root.Add(noLogs.Root);
        Settings.Bind(vm, nameof(vm.IsNoLogsNoteVisible), static s => s.IsNoLogsNoteVisible, visible =>
        {
            noLogs.IsOpen = visible;
            bars.Root.SetVisible(visible);
        });

        var protocolLog = Settings.Switch(vm, Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            nameof(vm.IsProtocolLogEnabled), static s => s.IsProtocolLogEnabled, static (s, v) => s.IsProtocolLogEnabled = v);
        var export = WinoSettingsRows.Action(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload);
        WinoSettingsRows.Suffix(export, WinoA11y.Rename(Settings.Button(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand), Translator.ProtocolLog_ExportAction));
        WinoSettingsRows.Suffix(export, WinoA11y.Rename(Settings.Button(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand), Translator.ProtocolLog_UploadAction));
        Page.AddGroup(Translator.AccountHub_TroubleshootingTitle, null, protocolLog, export);
    }
}
