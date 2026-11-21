using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>The To Do app of an account (Windows AccountToDoSettingsPage): where its task lists sync.</summary>
public sealed class AccountToDoPageViewController(AccountToDoSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : AccountAppPageViewController<AccountToDoSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var empty = new WinoSettingsCard(Translator.AccountApps_NoTaskLists, null, WinoIconGlyph.Checkmark);
        Bind.Visible(empty, vm, nameof(vm.HasTaskLists), s => !s.HasTaskLists);
        var lists = AddGroup(Translator.AccountApps_TaskListsHeader, empty);
        Bind.Collection(vm.TaskLists, () => AccountCollectionRows.Fill(lists, empty, vm.TaskLists.ToList(), WinoIconGlyph.Checkmark));

        Add(Caption(Translator.AccountApps_ToDoAppWideNote));
    }
}
