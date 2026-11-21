using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>Troubleshooting of an account (Windows AccountTroubleshootingPage): the protocol log and its export.</summary>
public sealed class AccountTroubleshootingPageViewController(AccountTroubleshootingPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountTroubleshootingPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var noLogs = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.AccountTroubleshooting_NoLogsMessage);
        noLogs.IsClosable = false;
        Bind.Visible(noLogs, vm, nameof(vm.IsNoLogsNoteVisible), s => s.IsNoLogsNoteVisible);
        Add(noLogs);

        var protocolLog = Card(Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            Bind.Switch(vm, nameof(vm.IsProtocolLogEnabled), s => s.IsProtocolLogEnabled, (s, v) => s.IsProtocolLogEnabled = v, Translator.ProtocolLog_EnableTitle));
        var export = Card(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload,
            Row(Bind.Button(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand),
                Bind.Button(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand)));
        AddGroup(Translator.AccountHub_TroubleshootingTitle, protocolLog, export);
    }
}
