using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>The To Do app of an account (Windows AccountToDoSettingsPage, macOS AccountToDoPageViewController): where its task lists sync.</summary>
internal sealed class AccountToDoSettingsPage(AccountToDoSettingsPageViewModel viewModel, WinoViewServices services)
    : AccountAppPage<AccountToDoSettingsPageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _lists;

    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var empty = WinoSettingsRows.Action(Translator.AccountApps_NoTaskLists, null, WinoIconGlyph.Checkmark);
        Settings.Visible(empty, vm, nameof(vm.HasTaskLists), static s => !s.HasTaskLists);
        var lists = _lists = Page.AddGroup(Translator.AccountApps_TaskListsHeader, null, empty);
        Settings.Collection(vm.TaskLists, () =>
        {
            AccountCollectionRows.Fill(lists, empty, [.. vm.TaskLists], WinoIconGlyph.Checkmark);
            empty.SetVisible(!vm.HasTaskLists);
        });
    }

#if DEBUG
    internal override string DebugDescribeContent()
        => $"lists={_lists?.Count - 1} " + string.Join(" ; ", ViewModel.TaskLists.Select(static item => $"{item.Title}|{item.Detail}")) + "\n";
#endif
}
