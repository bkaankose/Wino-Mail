using System.Collections.ObjectModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// What Wino remembers (Windows VoiceBrainPage), under Voice Control in Personal mode: the learned people, accounts,
/// folders and defaults with a forget button each, the remembered mailboxes, and the two ways to forget everything.
/// </summary>
public sealed class VoiceBrainPageViewController(VoiceBrainPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<VoiceBrainPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        spinner.StartAnimation(null);
        var loading = Row(spinner, Caption(Translator.VoiceBrain_Loading));
        Add(Bind.Visible(loading, vm, nameof(vm.IsLoading), s => s.IsLoading));

        var offline = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.VoiceBrain_Offline);
        offline.IsClosable = false;
        offline.ActionTitle = Translator.Buttons_TryAgain;
        offline.ActionInvoked += (_, _) => vm.RefreshCommand.Execute(null);
        Add(Bind.Visible(offline, vm, nameof(vm.IsOffline), s => s.IsOffline));

        var failed = InfoBar(WinoInfoBarSeverity.Error, null, Translator.VoiceBrain_ActionFailed);
        failed.IsClosable = false;
        Add(Bind.Visible(failed, vm, nameof(vm.IsActionFailed), s => s.IsActionFailed));

        var empty = Add(Group(null, Card(Translator.VoiceBrain_Empty_Title, Translator.VoiceBrain_Empty_Description, WinoIconGlyph.Lightbulb)));
        void UpdateEmpty() => empty.Hidden = !vm.IsEmpty;
        Bind.Bind(vm, nameof(vm.IsLoading), s => s.IsLoading, _ => UpdateEmpty());
        Bind.Bind(vm, nameof(vm.HasLoaded), s => s.HasLoaded, _ => UpdateEmpty());
        Bind.Bind(vm, nameof(vm.HasContent), s => s.HasContent, _ => UpdateEmpty());

        AddFactGroup(Translator.VoiceBrain_Group_People, vm.People);
        AddFactGroup(Translator.VoiceBrain_Group_Accounts, vm.Accounts);
        AddFactGroup(Translator.VoiceBrain_Group_Folders, vm.Folders);
        AddFactGroup(Translator.VoiceBrain_Group_Defaults, vm.Defaults);

        var sections = Add(Group(Translator.VoiceBrain_Group_Mailboxes));
        Bind.Collection(vm.Sections, () =>
        {
            sections.Clear();
            foreach (var section in vm.Sections)
                sections.Add(Card(section.Title, section.Detail, WinoIconGlyph.Mail));
            sections.Hidden = vm.Sections.Count == 0;
        });

        var deleteAll = Bind.Button(Translator.VoiceBrain_DeleteAll_Button, vm.DeleteEverythingCommand);
        var turnOff = Bind.Button(Translator.VoiceBrain_TurnOff_Button, vm.TurnOffPersonalModeCommand);
        var forget = Group(Translator.VoiceBrain_Group_Forget,
            Card(Translator.VoiceBrain_DeleteAll_Title, Translator.VoiceBrain_DeleteAll_Description, WinoIconGlyph.Delete, deleteAll),
            Card(Translator.VoiceBrain_TurnOff_Title, Translator.VoiceBrain_TurnOff_Description, WinoIconGlyph.Power, turnOff));
        Add(Bind.Visible(forget, vm, nameof(vm.HasLoaded), s => s.HasLoaded));
    }

    /// <summary>One group of learned facts; each row forgets its fact.</summary>
    private void AddFactGroup(string title, ObservableCollection<VoiceBrainFactItem> items)
    {
        var group = Add(Group(title));
        Bind.Collection(items, () =>
        {
            group.Clear();
            foreach (var item in items)
            {
                var delete = Bind.Button(string.Empty, item.DeleteCommand, icon: WinoIconGlyph.Delete);
                delete.ToolTip = item.DeleteAutomationName;
                WinoAccessibility.Label(delete, item.DeleteAutomationName);
                group.Add(Card(item.Title, item.HasDetail ? item.Detail : null, WinoIconGlyph.None, delete));
            }

            group.Hidden = items.Count == 0;
        });
    }
}
