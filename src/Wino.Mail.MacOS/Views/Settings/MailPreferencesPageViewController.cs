using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Mail preferences: startup mailbox, sync interval, search mode, undo, mark as read and mail behaviour (Windows MailPreferencesPage).</summary>
public sealed class MailPreferencesPageViewController(MailPreferencesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailPreferencesPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var startup = Bind.PopUp<MailPreferencesPageViewModel, IAccountProviderDetailViewModel>(vm,
            s => s.StartupAccounts.ToList(), item => item.StartupEntityTitle,
            nameof(vm.StartupAccount), s => s.StartupAccount, (s, v) => s.StartupAccount = v,
            width: 180, itemsCollection: vm.StartupAccounts);

        AddGroup(null,
            Card(Translator.SettingsStartupItem_Title, Translator.SettingsStartupItem_Description, WinoIconGlyph.Home, startup),
            Card(Translator.SettingsAppPreferences_EmailSyncInterval_Title, Translator.SettingsAppPreferences_EmailSyncInterval_Description, WinoIconGlyph.Sync,
                Bind.Stepper(vm, nameof(vm.EmailSyncIntervalMinutes), s => s.EmailSyncIntervalMinutes, (s, v) => s.EmailSyncIntervalMinutes = v, 1, 1440,
                    accessibilityLabel: Translator.SettingsAppPreferences_EmailSyncInterval_Title)),
            Card(Translator.SettingsAppPreferences_SearchMode_Title, Translator.SettingsAppPreferences_SearchMode_Description, WinoIconGlyph.Find,
                Bind.PopUp<MailPreferencesPageViewModel, string>(vm, s => s.SearchModes, item => item,
                    nameof(vm.SelectedDefaultSearchMode), s => s.SelectedDefaultSearchMode, (s, v) => s.SelectedDefaultSearchMode = v, width: 150)));

        var undoSend = Row(
            Bind.Switch(p, nameof(p.IsUndoSendingDraftsEnabled), s => s.IsUndoSendingDraftsEnabled, (s, v) => s.IsUndoSendingDraftsEnabled = v, Translator.SettingsAppPreferences_UndoSendingDrafts_Title),
            Bind.Enabled(Bind.Stepper(vm, nameof(vm.UndoSendingDraftsIntervalInSeconds), s => s.UndoSendingDraftsIntervalInSeconds, (s, v) => s.UndoSendingDraftsIntervalInSeconds = v, 1, 10),
                p, nameof(p.IsUndoSendingDraftsEnabled), s => s.IsUndoSendingDraftsEnabled));
        var undoDelete = Row(
            Bind.Switch(p, nameof(p.IsUndoDeletingMailsEnabled), s => s.IsUndoDeletingMailsEnabled, (s, v) => s.IsUndoDeletingMailsEnabled = v, Translator.SettingsAppPreferences_UndoDeletingMails_Title),
            Bind.Enabled(Bind.Stepper(vm, nameof(vm.UndoDeletingMailsIntervalInSeconds), s => s.UndoDeletingMailsIntervalInSeconds, (s, v) => s.UndoDeletingMailsIntervalInSeconds = v, 1, 10),
                p, nameof(p.IsUndoDeletingMailsEnabled), s => s.IsUndoDeletingMailsEnabled));

        // Mark as read: the Windows radio buttons become a pop-up; the delay applies to the timer option.
        var markOptions = new[] { Translator.SettingsMarkAsRead_WhenSelected, Translator.SettingsMarkAsRead_DontChange, Translator.SettingsMarkAsRead_Timer };
        var delay = Bind.Stepper(vm, nameof(vm.MarkAsDelay), s => s.MarkAsDelay, (s, v) => s.MarkAsDelay = v, 0, 120,
            accessibilityLabel: Translator.SettingsMarkAsRead_SecondsToWait);

        AddGroup(null,
            Expander(Translator.SettingsAppPreferences_UndoActions_Title, Translator.SettingsAppPreferences_UndoActions_Description, WinoIconGlyph.ArrowUndo, null,
                Card(Translator.SettingsAppPreferences_UndoSendingDrafts_Title, Translator.SettingsAppPreferences_UndoSendingDrafts_Description, WinoIconGlyph.Send, undoSend),
                Card(Translator.SettingsAppPreferences_UndoDeletingMails_Title, Translator.SettingsAppPreferences_UndoDeletingMails_Description, WinoIconGlyph.Delete, undoDelete)),
            Expander(Translator.SettingsMarkAsRead_Title, Translator.SettingsMarkAsRead_Description, WinoIconGlyph.MarkRead, null,
                Card(Translator.SettingsMarkAsRead_Title, null, WinoIconGlyph.None,
                    Bind.PopUp(vm, markOptions, nameof(vm.SelectedMarkAsOptionIndex), s => s.SelectedMarkAsOptionIndex, (s, v) => s.SelectedMarkAsOptionIndex = v, 200)),
                Bind.Enabled(Card(Translator.SettingsMarkAsRead_SecondsToWait, null, WinoIconGlyph.None, delay),
                    vm, nameof(vm.SelectedMarkAsOptionIndex), s => s.SelectedMarkAsOptionIndex == 2)));

        AddGroup(null,
            Card(Translator.SettingsAutoSelectNextItem_Title, Translator.SettingsAutoSelectNextItem_Description, WinoIconGlyph.List,
                Bind.Switch(p, nameof(p.AutoSelectNextItem), s => s.AutoSelectNextItem, (s, v) => s.AutoSelectNextItem = v, Translator.SettingsAutoSelectNextItem_Title)),
            Card(Translator.SettingsDeleteProtection_Title, Translator.SettingsDeleteProtection_Description, WinoIconGlyph.ShieldError,
                Bind.Switch(p, nameof(p.IsHardDeleteProtectionEnabled), s => s.IsHardDeleteProtectionEnabled, (s, v) => s.IsHardDeleteProtectionEnabled = v, Translator.SettingsDeleteProtection_Title)),
            Card(Translator.SettingsEmptyJunkFolderCommand_Title, Translator.SettingsEmptyJunkFolderCommand_Description, WinoIconGlyph.Delete,
                Bind.Switch(p, nameof(p.IsShowEmptyJunkFolderEnabled), s => s.IsShowEmptyJunkFolderEnabled, (s, v) => s.IsShowEmptyJunkFolderEnabled = v, Translator.SettingsEmptyJunkFolderCommand_Title)));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);
}
