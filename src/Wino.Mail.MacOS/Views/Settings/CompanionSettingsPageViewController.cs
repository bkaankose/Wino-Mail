using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Companion (Windows CompanionSettingsPage): turn the menu bar companion on or off, choose which
/// unread messages it shows and which sections appear. The global shortcut is Windows-only for now.
/// </summary>
public sealed class CompanionSettingsPageViewController(CompanionSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CompanionSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        AddIntro(Translator.CompanionSettings_MacOS_About_Description);

        // The menu bar icon follows General › App close behavior; say so when it is not shown.
        if (vm.PreferencesService.AppCloseBehavior != AppCloseBehavior.RunInBackgroundWithTrayIcon)
            Add(InfoBar(WinoInfoBarSeverity.Informational, null, Translator.CompanionSettings_MacOS_StatusItemHint));

        var enabled = Bind.Switch(vm, nameof(vm.IsCompanionEnabled), s => s.IsCompanionEnabled, (s, v) => s.IsCompanionEnabled = v,
            Translator.CompanionSettings_Enable_Title);
        AddGroup(null, Card(Translator.CompanionSettings_Enable_Title, Translator.CompanionSettings_MacOS_Enable_Description, WinoIconGlyph.Alert, enabled));

        var unreadBehavior = Bind.PopUp<CompanionSettingsPageViewModel, CompanionUnreadBehaviorOption>(vm,
            s => s.UnreadBehaviorOptions, option => option.DisplayText,
            nameof(vm.SelectedUnreadBehavior), s => s.SelectedUnreadBehavior, (s, v) => s.SelectedUnreadBehavior = v, width: 180);
        var unreadBehaviorCard = Bind.Enabled(
            Card(Translator.CompanionSettings_UnreadBehavior_Title, Translator.CompanionSettings_UnreadBehavior_Description, WinoIconGlyph.None, unreadBehavior),
            vm, nameof(vm.IsUnreadBehaviorEnabled), s => s.IsUnreadBehaviorEnabled);

        var content = Expander(Translator.CompanionSettings_Content_Title, Translator.CompanionSettings_Content_Description, WinoIconGlyph.Board, null,
            Card(Translator.CompanionSettings_ShowCalendar_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowCalendar), s => s.ShowCalendar, (s, v) => s.ShowCalendar = v, Translator.CompanionSettings_ShowCalendar_Title)),
            Card(Translator.CompanionSettings_ShowUnreadMail_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowUnreadMail), s => s.ShowUnreadMail, (s, v) => s.ShowUnreadMail = v, Translator.CompanionSettings_ShowUnreadMail_Title)),
            unreadBehaviorCard,
            Card(Translator.CompanionSettings_ShowTasks_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowTasks), s => s.ShowTasks, (s, v) => s.ShowTasks = v, Translator.CompanionSettings_ShowTasks_Title)),
            Card(Translator.CompanionSettings_ShowFavoriteContacts_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowFavoriteContacts), s => s.ShowFavoriteContacts, (s, v) => s.ShowFavoriteContacts = v, Translator.CompanionSettings_ShowFavoriteContacts_Title)));
        content.IsExpanded = true;
        AddGroup(null, Bind.Enabled(content, vm, nameof(vm.IsContentEnabled), s => s.IsContentEnabled));
    }
}
