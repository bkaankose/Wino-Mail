using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// The Mail app of an account (Windows AccountMailSettingsPage, macOS AccountMailPageViewController): sending (sender name,
/// aliases, signature, sent copies), organizing (folder options with each folder's Synchronize switch, filters, categories,
/// Focused Inbox, Wino Intelligence, notifications), then POP3. The Windows Jump List and macOS Dock menu columns have no
/// Linux counterpart.
/// </summary>
internal sealed class AccountMailSettingsPage(AccountMailSettingsPageViewModel viewModel, WinoViewServices services)
    : AccountAppPage<AccountMailSettingsPageViewModel>(viewModel, services)
{
    private readonly List<Gtk.Widget> _folderWidgets = [];
    private readonly List<(IMailItemFolder Folder, Adw.SwitchRow Row)> _folderSwitches = [];
    private Adw.ExpanderRow? _folderOptions;
    private SettingsBinder? _folderRows;

    protected override void BuildAppSettings()
    {
        var vm = ViewModel;

        var initialSync = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.AccountDetailsPage_InitialSynchronization_Title) { IsClosable = true };
        Settings.Own(initialSync);
        var initialGroup = NonRowGroup(initialSync.Root);
        Settings.Bind(vm, nameof(vm.IsInitialSynchronizationSummaryVisible), static s => s.IsInitialSynchronizationSummaryVisible, visible =>
        {
            initialSync.Message = vm.InitialSynchronizationSummary;
            initialSync.IsOpen = visible;
            initialGroup.Root.SetVisible(visible);
        });
        initialSync.Closed += (_, _) => initialGroup.Root.SetVisible(false);

        var syncRange = vm.SyncRange;
        var rangeRow = Settings.Choice(syncRange, Translator.AccountMail_SyncRange_Title, Translator.AccountMail_SyncRange_Description, WinoIconGlyph.ArrowDownload,
            static s => s.Ranges, static option => option.DisplayText,
            nameof(syncRange.SelectedRange), static s => s.SelectedRange, static (s, v) => s.SelectedRange = v,
            itemsCollection: syncRange.Ranges);
        Settings.Subtitle(rangeRow, syncRange, nameof(syncRange.StatusText),
            static s => s.IsStatusVisible ? s.StatusText : Translator.AccountMail_SyncRange_Description);
        Settings.Enabled(rangeRow, syncRange, nameof(syncRange.IsEnabled), static s => s.IsEnabled);
        WinoSettingsRows.Suffix(rangeRow, Settings.Button(Translator.AccountMail_SyncRange_Apply, syncRange.ApplyCommand, suggested: true));
        var rangeGroup = Page.AddGroup(null, null, rangeRow);
        Settings.Visible(rangeGroup.Root, syncRange, nameof(syncRange.IsVisible), static s => s.IsVisible);

        var providerManaged = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.AccountDetailsPage_SenderNameProviderManagedNote) { IsClosable = false };
        Settings.Own(providerManaged);
        var providerGroup = NonRowGroup(providerManaged.Root);
        Settings.Bind(vm, nameof(vm.IsProviderManagedSenderNameNoteVisible), static s => s.IsProviderManagedSenderNameNoteVisible, visible =>
        {
            providerManaged.IsOpen = visible;
            providerGroup.Root.SetVisible(visible);
        });

        var senderName = WinoSettingsRows.Action(Translator.AccountSettingsDialog_AccountName, Translator.AccountMail_SenderNameDescription, WinoIconGlyph.Person);
        WinoSettingsRows.Suffix(senderName, Settings.TextSuffix(vm, Translator.AccountSettingsDialog_AccountName, Translator.AccountSettingsDialog_AccountNamePlaceholder,
            nameof(vm.SenderName), static s => s.SenderName, static (s, v) => s.SenderName = v));
        Settings.Visible(senderName, vm, nameof(vm.IsSenderNameEditable), static s => s.IsSenderNameEditable);
        var aliases = Settings.Command(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand);
        var signature = WinoSettingsRows.Action(Translator.SettingsSignature_Title, Translator.SettingsSignature_Description, WinoIconGlyph.Signature);
        WinoSettingsRows.Suffix(signature, Settings.SwitchSuffix(vm, Translator.SettingsSignature_Title,
            nameof(vm.IsSignatureEnabled), static s => s.IsSignatureEnabled, static (s, v) => s.IsSignatureEnabled = v));
        var editSignature = WinoSettingsRows.Suffix(signature, WinoSettingsRows.ChevronButton(Translator.SettingsSignature_Title));
        Settings.Own(GtkSignals.OnClicked(editSignature, () =>
        {
            if (vm.EditSignatureCommand.CanExecute(null)) vm.EditSignatureCommand.Execute(null);
        }));
        var appendSent = Settings.Switch(vm, Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description,
            WinoIconGlyph.MailArrowUp, nameof(vm.IsAppendMessageSettingEnabled), static s => s.IsAppendMessageSettingEnabled, static (s, v) => s.IsAppendMessageSettingEnabled = v);
        Settings.Visible(appendSent, vm, nameof(vm.IsAppendMessageSettingVisible), static s => s.IsAppendMessageSettingVisible);
        Page.AddGroup(Translator.AccountMail_SendingHeader, null, senderName, aliases, signature, appendSent);

        _folderOptions = WinoSettingsRows.Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, false,
            Settings.Command(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
            Settings.Command(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand));
        Settings.Collection(vm.CurrentFolders, RebuildFolders);

        var focused = Settings.Switch(vm, Translator.SettingsFocusedInbox_Title, Translator.SettingsFocusedInbox_Description, WinoIconGlyph.MailInboxCheckmark,
            nameof(vm.IsFocusedInboxEnabled), static s => s.IsFocusedInboxEnabled, static (s, v) => s.IsFocusedInboxEnabled = v);
        Settings.Visible(focused, vm, nameof(vm.IsFocusedInboxSupported), static s => s.IsFocusedInboxSupported);
        var intelligence = Settings.Command(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand);
        Settings.Visible(intelligence, vm, nameof(vm.CanAccessWinoIntelligence), static s => s.CanAccessWinoIntelligence);
        var notifications = WinoSettingsRows.Expander(Translator.SettingsNotifications_Title, Translator.SettingsNotifications_Description, WinoIconGlyph.Alert, false,
            Settings.Command(Translator.SettingsUnreadBadges_Card_Title, Translator.SettingsUnreadBadges_Card_Description, WinoIconGlyph.AlertBadge, vm.ConfigureUnreadBadgesCommand),
            Settings.Switch(vm, Translator.SettingsHideNotificationContent_Title, Translator.SettingsHideNotificationContent_Description, WinoIconGlyph.EyeOff,
                nameof(vm.IsNotificationContentHidden), static s => s.IsNotificationContentHidden, static (s, v) => s.IsNotificationContentHidden = v));
        Page.AddGroup(Translator.AccountMail_OrganizeHeader, null,
            _folderOptions,
            Settings.Command(Translator.MailFilters_Title, Translator.MailFilters_AccountCardDescription, WinoIconGlyph.Filter, vm.ManageMailFiltersCommand),
            Settings.Command(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand),
            focused,
            intelligence,
            notifications);

        var pop3 = Page.AddGroup(Translator.AccountMail_Pop3Header, null,
            Settings.Switch(vm, Translator.POP3Setup_LeaveOnServerTitle, Translator.POP3Setup_LeaveOnServerDescription, WinoIconGlyph.ArrowDownload,
                nameof(vm.Pop3LeaveOnServer), static s => s.Pop3LeaveOnServer, static (s, v) => s.Pop3LeaveOnServer = v),
            Settings.Choice(vm, Translator.POP3Setup_DeleteAfterDaysTitle, Translator.POP3Setup_DeleteAfterDaysDescription, WinoIconGlyph.None, [.. vm.Pop3DeleteAfterDaysLabels],
                nameof(vm.SelectedPop3DeleteAfterDaysIndex), static s => s.SelectedPop3DeleteAfterDaysIndex, static (s, v) => s.SelectedPop3DeleteAfterDaysIndex = v),
            Settings.Switch(vm, Translator.POP3Setup_DeleteWithLocalTitle, Translator.POP3Setup_DeleteWithLocalDescription, WinoIconGlyph.None,
                nameof(vm.Pop3DeleteWithLocal), static s => s.Pop3DeleteWithLocal, static (s, v) => s.Pop3DeleteWithLocal = v));
        Settings.Visible(pop3.Root, vm, nameof(vm.IsPop3), static s => s.IsPop3);
    }

    /// <summary>Every folder with its Synchronize switch, indented by depth; a folder that cannot hold mail has none (as on Windows).</summary>
    private void RebuildFolders()
    {
        if (_folderOptions is null || IsReleased) return;
        _folderRows?.Scope.Dispose();
        var rows = _folderRows = Settings.Child();
        foreach (var widget in _folderWidgets) _folderOptions.Remove(widget);
        _folderWidgets.Clear();
        _folderSwitches.Clear();

        void Add(IMailItemFolder folder, int depth)
        {
            var name = folder.ResolvedDisplayName ?? string.Empty;
            Adw.ActionRow row;
            if (folder.IsSelectable)
            {
                var toggle = Adw.SwitchRow.New();
                toggle.SetUseMarkup(false);
                toggle.SetTitle(name);
                toggle.SetSubtitle(Translator.SettingsFolderSynchronize_Title);
                toggle.SetActive(folder.IsSynchronizationEnabled);
                rows.Own(GtkSignals.OnNotify(toggle, "active", () =>
                {
                    var enabled = toggle.GetActive();
                    if (enabled == folder.IsSynchronizationEnabled) return;
                    folder.IsSynchronizationEnabled = enabled;
                    Observe(ToggleFolderAsync(folder, enabled, toggle));
                }));
                _folderSwitches.Add((folder, toggle));
                row = toggle;
            }
            else
            {
                row = WinoSettingsRows.Action(name);
            }
            WinoSettingsRows.SetGlyph(row, SpecialFolderGlyphs.GetGlyphOrFolder(folder));
            row.SetMarginStart(depth * 16);
            _folderOptions.AddRow(row);
            _folderWidgets.Add(row);
            foreach (var child in folder.ChildFolders ?? []) Add(child, depth + 1);
        }

        foreach (var folder in ViewModel.CurrentFolders.ToList()) Add(folder, 0);
    }

    private async Task ToggleFolderAsync(IMailItemFolder folder, bool enabled, Adw.SwitchRow toggle)
    {
        try { await ViewModel.FolderSyncToggledAsync(folder, enabled); }
        catch
        {
            folder.IsSynchronizationEnabled = !enabled;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!IsReleased) toggle.SetActive(!enabled);
            });
            throw;
        }
    }

#if DEBUG
    internal bool DebugToggleFolder(string name, bool on)
    {
        foreach (var (folder, row) in _folderSwitches)
        {
            if (!string.Equals(folder.ResolvedDisplayName, name, StringComparison.OrdinalIgnoreCase)) continue;
            row.SetActive(on);
            return true;
        }
        return false;
    }

    internal override string DebugDescribeContent()
    {
        var vm = ViewModel;
        return $"sender={vm.SenderName} editable={vm.IsSenderNameEditable} signature={vm.IsSignatureEnabled} append={vm.IsAppendMessageSettingVisible}:{vm.IsAppendMessageSettingEnabled}"
               + $" focused={vm.IsFocusedInboxSupported} pop3={vm.IsPop3}:{vm.Pop3LeaveOnServer}:{vm.SelectedPop3DeleteAfterDaysIndex}:{vm.Pop3DeleteWithLocal}\n"
               + "folders=" + string.Join(" ; ", _folderSwitches.Select(static entry => $"{entry.Folder.ResolvedDisplayName}:{(entry.Row.GetActive() ? "on" : "off")}")) + "\n";
    }
#endif
}
