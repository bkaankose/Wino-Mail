using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Messages.Mails
{
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
}
