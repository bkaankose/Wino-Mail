using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>The Mail app of an account (Windows AccountMailSettingsPage): sending, folders and notifications, then POP3.</summary>
public sealed class AccountMailPageViewController(AccountMailSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : AccountAppPageViewController<AccountMailSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private const double FolderCheckColumnWidth = 90;
    private readonly NSStackView _folderRows = WinoLayout.VStack(0);
    private readonly List<NSButton> _dockMenuCheckboxes = [];

    protected override void BuildAppSettings()
    {
        var vm = ViewModel;

        var initialSync = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountDetailsPage_InitialSynchronization_Title, null);
        initialSync.IsClosable = true;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            initialSync.Message = vm.InitialSynchronizationSummary;
            initialSync.Hidden = !vm.IsInitialSynchronizationSummaryVisible;
        });
        Add(initialSync);

        var providerManaged = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.AccountDetailsPage_SenderNameProviderManagedNote);
        providerManaged.IsClosable = false;
        Bind.Visible(providerManaged, vm, nameof(vm.IsProviderManagedSenderNameNoteVisible), s => s.IsProviderManagedSenderNameNoteVisible);
        var senderName = Card(Translator.AccountSettingsDialog_AccountName, Translator.AccountMail_SenderNameDescription, WinoIconGlyph.Person,
            Bind.TextField(vm, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder));
        Bind.Visible(senderName, vm, nameof(vm.IsSenderNameEditable), s => s.IsSenderNameEditable);
        var aliases = CommandCard(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand);
        var signature = NavigationCard(Translator.SettingsSignature_Title, Translator.SettingsSignature_Description, WinoIconGlyph.Signature,
            () => { if (vm.EditSignatureCommand.CanExecute(null)) vm.EditSignatureCommand.Execute(null); },
            Bind.Switch(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, (s, v) => s.IsSignatureEnabled = v, Translator.SettingsSignature_Title));
        var appendSent = Card(Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description, WinoIconGlyph.MailArrowUp,
            Bind.Switch(vm, nameof(vm.IsAppendMessageSettingEnabled), s => s.IsAppendMessageSettingEnabled, (s, v) => s.IsAppendMessageSettingEnabled = v, Translator.SettingsAccountManagementAppendMessage_Title));
        Bind.Visible(appendSent, vm, nameof(vm.IsAppendMessageSettingVisible), s => s.IsAppendMessageSettingVisible);
        AddGroup(Translator.AccountMail_SendingHeader, providerManaged, senderName, aliases, signature, appendSent);

        var folderTable = WinoLayout.VStack(0, FolderColumnHeader(), _folderRows);
        folderTable.Alignment = NSLayoutAttribute.Leading;
        _folderRows.Alignment = NSLayoutAttribute.Leading;
        _folderRows.WidthAnchor.ConstraintEqualTo(folderTable.WidthAnchor).Active = true;
        Bind.Collection(vm.CurrentFolders, RebuildFolderRows);

        var folderOptions = Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, null,
            CommandCard(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
            CommandCard(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand),
            folderTable);

        var dockMenu = Card(Translator.SettingsDockMenu_Title, Translator.SettingsDockMenu_Description, WinoIconGlyph.TaskList,
            Bind.Switch(vm, nameof(vm.IsJumpListEnabled), s => s.IsJumpListEnabled, (s, v) => s.IsJumpListEnabled = v, Translator.SettingsDockMenu_Title));
        Bind.Bind(vm, nameof(vm.IsJumpListEnabled), s => s.IsJumpListEnabled, enabled =>
        {
            foreach (var checkbox in _dockMenuCheckboxes) checkbox.Enabled = enabled;
        });
        var notifications = Expander(Translator.SettingsNotificationsAndDock_Title, Translator.SettingsNotificationsAndDock_Description, WinoIconGlyph.Alert, null,
            CommandCard(Translator.SettingsUnreadBadges_Card_Title, Translator.SettingsUnreadBadges_Card_Description, WinoIconGlyph.AlertBadge, vm.ConfigureUnreadBadgesCommand),
            dockMenu,
            Card(Translator.SettingsHideNotificationContent_Title, Translator.SettingsHideNotificationContent_Description, WinoIconGlyph.EyeOff,
                Bind.Switch(vm, nameof(vm.IsNotificationContentHidden), s => s.IsNotificationContentHidden, (s, v) => s.IsNotificationContentHidden = v, Translator.SettingsHideNotificationContent_Title)));

        var focused = Card(Translator.SettingsFocusedInbox_Title, Translator.SettingsFocusedInbox_Description, WinoIconGlyph.MailInboxCheckmark,
            Bind.Switch(vm, nameof(vm.IsFocusedInboxEnabled), s => s.IsFocusedInboxEnabled, (s, v) => s.IsFocusedInboxEnabled = v, Translator.SettingsFocusedInbox_Title));
        Bind.Visible(focused, vm, nameof(vm.IsFocusedInboxSupported), s => s.IsFocusedInboxSupported);
        var intelligence = CommandCard(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand);
        Bind.Visible(intelligence, vm, nameof(vm.CanAccessWinoIntelligence), s => s.CanAccessWinoIntelligence);
        AddGroup(Translator.AccountMail_OrganizeHeader,
            folderOptions,
            CommandCard(Translator.MailFilters_Title, Translator.MailFilters_AccountCardDescription, WinoIconGlyph.Filter, vm.ManageMailFiltersCommand),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand),
            focused,
            intelligence,
            notifications);

        var pop3LeaveOnServer = Card(Translator.POP3Setup_LeaveOnServerTitle, Translator.POP3Setup_LeaveOnServerDescription, WinoIconGlyph.ArrowDownload,
            Bind.Switch(vm, nameof(vm.Pop3LeaveOnServer), s => s.Pop3LeaveOnServer, (s, v) => s.Pop3LeaveOnServer = v, Translator.POP3Setup_LeaveOnServerTitle));
        var pop3DeleteAfter = Card(Translator.POP3Setup_DeleteAfterDaysTitle, Translator.POP3Setup_DeleteAfterDaysDescription, WinoIconGlyph.None,
            Bind.PopUp<AccountMailSettingsPageViewModel>(vm, vm.Pop3DeleteAfterDaysLabels, nameof(vm.SelectedPop3DeleteAfterDaysIndex),
                s => s.SelectedPop3DeleteAfterDaysIndex, (s, v) => s.SelectedPop3DeleteAfterDaysIndex = v, width: 180));
        var pop3DeleteWithLocal = Card(Translator.POP3Setup_DeleteWithLocalTitle, Translator.POP3Setup_DeleteWithLocalDescription, WinoIconGlyph.None,
            Bind.Switch(vm, nameof(vm.Pop3DeleteWithLocal), s => s.Pop3DeleteWithLocal, (s, v) => s.Pop3DeleteWithLocal = v, Translator.POP3Setup_DeleteWithLocalTitle));
        var pop3 = AddGroup(Translator.AccountMail_Pop3Header, pop3LeaveOnServer, pop3DeleteAfter, pop3DeleteWithLocal);
        Bind.Visible(pop3, vm, nameof(vm.IsPop3), s => s.IsPop3);
    }

    private NSView FolderColumnHeader()
    {
        var row = WinoLayout.HStack(WinoStyle.Space4, WinoLayout.Spacer(),
            FolderColumnLabel(Translator.SettingsFolderSynchronize_Title), FolderColumnLabel(Translator.SettingsFolderDockMenu_Title));
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 10, WinoSettingsStyle.NestedIndent, 4, WinoSettingsStyle.CardPadding);
        return host;
    }

    private static NSTextField FolderColumnLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.Alignment = NSTextAlignment.Center;
        label.WidthAnchor.ConstraintEqualTo((nfloat)FolderCheckColumnWidth).Active = true;
        return label;
    }

    /// <summary>The Windows folder tree: every folder with its Synchronize and Dock menu (Jump List) checkboxes.</summary>
    private void RebuildFolderRows()
    {
        _dockMenuCheckboxes.Clear();
        foreach (var view in _folderRows.ArrangedSubviews) { _folderRows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var folder in ViewModel.CurrentFolders.ToList()) AddFolderRows(folder, 0);
    }

    private void AddFolderRows(IMailItemFolder folder, int depth)
    {
        var row = FolderRow(folder, depth);
        _folderRows.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(_folderRows.WidthAnchor).Active = true;
        foreach (var child in folder.ChildFolders ?? []) AddFolderRows(child, depth + 1);
    }

    private NSView FolderRow(IMailItemFolder folder, int depth)
    {
        var icon = new WinoIconView(SpecialFolderGlyphs.GetGlyphOrFolder(folder), 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(folder.ResolvedDisplayName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        // Folders that cannot hold mail (IsSelectable false) have no checkboxes on Windows either.
        var (_, sync) = FolderCheckbox(folder, Translator.SettingsFolderSynchronize_Title, folder.IsSynchronizationEnabled,
            isOn => ViewModel.FolderSyncToggledAsync(folder, isOn));
        var (dockCheckbox, dock) = FolderCheckbox(folder, Translator.SettingsFolderDockMenu_Title, folder.IsJumpListEnabled,
            isOn => ViewModel.FolderJumpListToggledAsync(folder, isOn));
        dockCheckbox.Enabled = ViewModel.IsJumpListEnabled;
        _dockMenuCheckboxes.Add(dockCheckbox);

        var row = WinoLayout.HStack(WinoStyle.Space2, icon, name, sync, dock);
        row.Alignment = NSLayoutAttribute.CenterY;
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 6, WinoSettingsStyle.NestedIndent + depth * 16, 6, WinoSettingsStyle.CardPadding);
        return host;
    }

    /// <summary>A centered checkbox in a fixed-width column host.</summary>
    private (NSButton Checkbox, NSView Host) FolderCheckbox(IMailItemFolder folder, string column, bool isOn, Func<bool, Task> toggled)
    {
        var checkbox = NSButton.CreateCheckbox(string.Empty, () => { });
        checkbox.State = isOn ? NSCellStateValue.On : NSCellStateValue.Off;
        checkbox.Activated += async (_, _) =>
        {
            try { await toggled(checkbox.State == NSCellStateValue.On); }
            catch (Exception ex) { ReportError(ex); }
        };
        checkbox.Hidden = !folder.IsSelectable;
        WinoAccessibility.Label(checkbox, $"{folder.ResolvedDisplayName}, {column}");
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(checkbox);
        checkbox.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            host.WidthAnchor.ConstraintEqualTo((nfloat)FolderCheckColumnWidth),
            checkbox.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            checkbox.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            checkbox.BottomAnchor.ConstraintEqualTo(host.BottomAnchor)
        ]);
        return (checkbox, host);
    }
}
