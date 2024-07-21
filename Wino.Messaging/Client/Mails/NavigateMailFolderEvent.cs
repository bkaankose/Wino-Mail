using System.Threading.Tasks;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Navigation;

namespace Wino.Messaging.Client.Mails
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
