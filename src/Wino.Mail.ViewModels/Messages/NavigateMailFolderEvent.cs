using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// Selects the given FolderMenuItem in the shell folders list.
/// </summary>
public class NavigateMailFolderEvent : NavigateMailFolderEventArgs
{
    public NavigateMailFolderEvent(IBaseFolderMenuItem baseFolderMenuItem, TaskCompletionSource<bool> folderInitLoadAwaitTask = null)
        : base(baseFolderMenuItem, folderInitLoadAwaitTask)
    {
    }
}
