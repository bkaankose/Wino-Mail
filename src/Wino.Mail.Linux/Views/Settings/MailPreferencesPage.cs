using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>Mail preferences: startup mailbox, sync interval, search mode, undo, mark as read and mail behaviour (Windows MailPreferencesPage).</summary>
internal sealed class MailPreferencesPage(MailPreferencesPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<MailPreferencesPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        AddGroup(null,
            Settings.Choice<MailPreferencesPageViewModel, IAccountProviderDetailViewModel>(vm, Translator.SettingsStartupItem_Title, Translator.SettingsStartupItem_Description, WinoIconGlyph.Home,
                static s => s.StartupAccounts.ToList(), static item => item.StartupEntityTitle,
                nameof(vm.StartupAccount), static s => s.StartupAccount, static (s, v) => s.StartupAccount = v,
                itemsCollection: vm.StartupAccounts),
            vm.IsSyncIntervalAvailable
                ? Settings.Number(vm, Translator.SettingsAppPreferences_EmailSyncInterval_Title, Translator.SettingsAppPreferences_EmailSyncInterval_Description, WinoIconGlyph.Sync,
                    nameof(vm.EmailSyncIntervalMinutes), static s => s.EmailSyncIntervalMinutes, static (s, v) => s.EmailSyncIntervalMinutes = v, 1, 1440)
                : null,
            Settings.Choice<MailPreferencesPageViewModel, string>(vm, Translator.SettingsAppPreferences_SearchMode_Title, Translator.SettingsAppPreferences_SearchMode_Description, WinoIconGlyph.Find,
                static s => s.SearchModes, static item => item,
                nameof(vm.SelectedDefaultSearchMode), static s => s.SelectedDefaultSearchMode, static (s, v) => s.SelectedDefaultSearchMode = v));

        var undoSend = WinoSettingsRows.Action(Translator.SettingsAppPreferences_UndoSendingDrafts_Title, Translator.SettingsAppPreferences_UndoSendingDrafts_Description, WinoIconGlyph.Send);
        WinoSettingsRows.Suffix(undoSend, Settings.Enabled(
            Settings.NumberSuffix(vm, Translator.SettingsAppPreferences_UndoSendingDrafts_Title, nameof(vm.UndoSendingDraftsIntervalInSeconds),
                static s => s.UndoSendingDraftsIntervalInSeconds, static (s, v) => s.UndoSendingDraftsIntervalInSeconds = v, 1, 10),
            p, nameof(p.IsUndoSendingDraftsEnabled), static s => s.IsUndoSendingDraftsEnabled));
        WinoSettingsRows.Suffix(undoSend, Settings.SwitchSuffix(p, Translator.SettingsAppPreferences_UndoSendingDrafts_Title, nameof(p.IsUndoSendingDraftsEnabled),
            static s => s.IsUndoSendingDraftsEnabled, static (s, v) => s.IsUndoSendingDraftsEnabled = v));

        var undoDelete = WinoSettingsRows.Action(Translator.SettingsAppPreferences_UndoDeletingMails_Title, Translator.SettingsAppPreferences_UndoDeletingMails_Description, WinoIconGlyph.Delete);
        WinoSettingsRows.Suffix(undoDelete, Settings.Enabled(
            Settings.NumberSuffix(vm, Translator.SettingsAppPreferences_UndoDeletingMails_Title, nameof(vm.UndoDeletingMailsIntervalInSeconds),
                static s => s.UndoDeletingMailsIntervalInSeconds, static (s, v) => s.UndoDeletingMailsIntervalInSeconds = v, 1, 10),
            p, nameof(p.IsUndoDeletingMailsEnabled), static s => s.IsUndoDeletingMailsEnabled));
        WinoSettingsRows.Suffix(undoDelete, Settings.SwitchSuffix(p, Translator.SettingsAppPreferences_UndoDeletingMails_Title, nameof(p.IsUndoDeletingMailsEnabled),
            static s => s.IsUndoDeletingMailsEnabled, static (s, v) => s.IsUndoDeletingMailsEnabled = v));

        string[] markOptions = [Translator.SettingsMarkAsRead_WhenSelected, Translator.SettingsMarkAsRead_DontChange, Translator.SettingsMarkAsRead_Timer];
        AddGroup(null,
            WinoSettingsRows.Expander(Translator.SettingsAppPreferences_UndoActions_Title, Translator.SettingsAppPreferences_UndoActions_Description, WinoIconGlyph.ArrowUndo, false,
                undoSend, undoDelete),
            WinoSettingsRows.Expander(Translator.SettingsMarkAsRead_Title, Translator.SettingsMarkAsRead_Description, WinoIconGlyph.MarkRead, false,
                Settings.Choice(vm, Translator.SettingsMarkAsRead_Title, null, WinoIconGlyph.None, markOptions,
                    nameof(vm.SelectedMarkAsOptionIndex), static s => s.SelectedMarkAsOptionIndex, static (s, v) => s.SelectedMarkAsOptionIndex = v),
                Settings.Enabled(Settings.Number(vm, Translator.SettingsMarkAsRead_SecondsToWait, null, WinoIconGlyph.None,
                        nameof(vm.MarkAsDelay), static s => s.MarkAsDelay, static (s, v) => s.MarkAsDelay = v, 0, 120),
                    vm, nameof(vm.SelectedMarkAsOptionIndex), static s => s.SelectedMarkAsOptionIndex == 2)));

        AddGroup(null,
            Settings.Switch(p, Translator.SettingsAutoSelectNextItem_Title, Translator.SettingsAutoSelectNextItem_Description, WinoIconGlyph.List,
                nameof(p.AutoSelectNextItem), static s => s.AutoSelectNextItem, static (s, v) => s.AutoSelectNextItem = v),
            Settings.Switch(p, Translator.SettingsDeleteProtection_Title, Translator.SettingsDeleteProtection_Description, WinoIconGlyph.ShieldError,
                nameof(p.IsHardDeleteProtectionEnabled), static s => s.IsHardDeleteProtectionEnabled, static (s, v) => s.IsHardDeleteProtectionEnabled = v),
            Settings.Switch(p, Translator.SettingsEmptyJunkFolderCommand_Title, Translator.SettingsEmptyJunkFolderCommand_Description, WinoIconGlyph.Delete,
                nameof(p.IsShowEmptyJunkFolderEnabled), static s => s.IsShowEmptyJunkFolderEnabled, static (s, v) => s.IsShowEmptyJunkFolderEnabled = v));
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);
}
