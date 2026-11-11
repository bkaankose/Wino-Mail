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
/// What Wino remembers (Windows VoiceBrainPage), under Voice Control: the learned people, accounts, folders and
/// defaults with a forget button each, memory sync across devices, and the way to forget everything.
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

        AddSyncGroup();

        var deleteAll = Bind.Button(Translator.VoiceBrain_DeleteAll_Button, vm.DeleteEverythingCommand);
        var forget = Group(Translator.VoiceBrain_Group_Forget,
            Card(Translator.VoiceBrain_DeleteAll_Title, Translator.VoiceBrain_DeleteAll_Description, WinoIconGlyph.Delete, deleteAll));
        Add(Bind.Visible(forget, vm, nameof(vm.HasLoaded), s => s.HasLoaded));
    }

    /// <summary>
    /// Memory sync: on or off with its state, a password chosen on this device (never shown or kept by the page), and
    /// the two answers when the server copy was made with another password.
    /// </summary>
    private void AddSyncGroup()
    {
        var vm = ViewModel;
        var turnOn = Bind.Visible(Bind.Button(Translator.VoiceBrain_Sync_TurnOn_Button, vm.TurnOnSyncCommand, primary: true), vm, nameof(vm.IsSyncOff), s => s.IsSyncOff);
        var change = Bind.Visible(Bind.Button(Translator.VoiceBrain_Sync_ChangePassword_Button, vm.ChangeSyncPasswordCommand), vm, nameof(vm.IsSyncOn), s => s.IsSyncOn);
        var turnOff = Bind.Visible(Bind.Button(Translator.VoiceBrain_Sync_TurnOff_Button, vm.TurnOffSyncCommand), vm, nameof(vm.IsSyncOn), s => s.IsSyncOn);
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        spinner.StartAnimation(null);
        Bind.Visible(spinner, vm, nameof(vm.IsSyncBusy), s => s.IsSyncBusy);
        var card = Card(Translator.VoiceBrain_Sync_Title, vm.SyncDescription, WinoIconGlyph.Key, Row(spinner, turnOn, change, turnOff));
        Bind.Bind(vm, nameof(vm.SyncDescription), s => s.SyncDescription, value => card.Description = value);

        var mismatch = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.VoiceBrain_Sync_WrongPassword);
        mismatch.IsClosable = false;
        mismatch.ActionTitle = Translator.VoiceBrain_Sync_EnterPassword_Button;
        mismatch.ActionInvoked += (_, _) => vm.EnterSyncPasswordCommand.Execute(null);
        var replace = Bind.Button(Translator.VoiceBrain_Sync_Replace_Button, vm.ReplaceServerCopyCommand);
        var mismatchRow = Bind.Visible(WinoLayout.VStack(WinoStyle.Space2, mismatch, replace), vm, nameof(vm.IsSyncPasswordMismatch), s => s.IsSyncPasswordMismatch);

        var failed = InfoBar(WinoInfoBarSeverity.Error, null, Translator.VoiceBrain_Sync_Failed);
        failed.IsClosable = false;

        Add(Group(Translator.VoiceBrain_Group_Sync, card, mismatchRow,
            Bind.Visible(failed, vm, nameof(vm.IsSyncFailed), s => s.IsSyncFailed)));
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
