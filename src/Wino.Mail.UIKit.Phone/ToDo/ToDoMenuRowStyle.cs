using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Phone.ToDo;

/// <summary>
/// The To Do tab's Lists rows (iOS design decisions; the shared <see cref="ToDoPageViewModel.ShellMenu"/> in the Windows
/// order): New list, My Day, Planned, Important, then the accounts with their groups and lists, with separators. The
/// current row is the smart view or list the tasks page shows.
/// </summary>
internal sealed class ToDoMenuRowStyle : IModeMenuRowStyle
{
    public string Title => Translator.IOS_ToDoListsTitle;

    public WinoShellRowKind? Kind(IMenuItem item) => item switch
    {
        NewTaskListMenuItem => WinoShellRowKind.NewItem,
        ShellSectionHeaderMenuItem => WinoShellRowKind.Header,
        _ => WinoShellRowKind.Folder
    };

    public void Configure(WinoShellRowCell cell, IMenuItem item, int depth)
    {
        int indent = Math.Max(0, depth - 1);
        bool hasChildren = Wino.Mail.ViewModels.Data.ShellMenuItemRules.Children(item).Any(Wino.Mail.ViewModels.Data.ShellMenuItemRules.IsShown);
        switch (item)
        {
            case NewTaskListMenuItem:
                cell.ConfigureNewItem(Translator.ToDoPage_NewList, WinoIconGlyph.Add);
                break;
            case ShellSectionHeaderMenuItem header:
                cell.ConfigureHeader(header.Title);
                break;
            case MyDayTaskMenuItem myDay:
                cell.ConfigureFolder(WinoIconGlyph.WeatherSunny, Translator.ToDoPage_MyDay, myDay.Count, indent);
                break;
            case PlannedTaskMenuItem planned:
                cell.ConfigureFolder(WinoIconGlyph.Calendar, Translator.ToDoPage_Planned, planned.Count, indent);
                break;
            case ImportantTaskMenuItem important:
                cell.ConfigureFolder(WinoIconGlyph.Star, Translator.ToDoPage_Important, important.Count, indent);
                break;
            case AccountTaskListAccountMenuItem account:
                cell.ConfigureFolder(WinoIconGlyph.Person, account.AccountName ?? string.Empty, 0, indent);
                break;
            case AccountTaskListGroupMenuItem group:
                cell.ConfigureFolder(WinoIconGlyph.PanelLeft, group.Title ?? string.Empty, 0, indent, hasChildren ? group.IsExpanded : null);
                break;
            case AccountTaskListMenuItem list:
                cell.ConfigureFolder(WinoIconGlyph.Dot, list.Title ?? string.Empty, list.Count, indent);
                break;
            default:
                cell.ConfigureFolder(WinoIconGlyph.List, item.ToString() ?? string.Empty, 0, indent);
                break;
        }
    }

    public bool IsCurrent(IMenuItem item, object? selectedMenuItem, object? contentViewModel)
    {
        if (contentViewModel is not ToDoPageViewModel tasks) return ReferenceEquals(item, selectedMenuItem);
        return item switch
        {
            AccountTaskListMenuItem list => tasks.SelectedList?.Id == list.Parameter.Id,
            TaskSmartViewMenuItem view => tasks.SelectedList is null && tasks.SelectedView == view.Parameter,
            _ => false
        };
    }
}
