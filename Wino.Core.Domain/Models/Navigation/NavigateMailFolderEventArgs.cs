using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Navigation
{
    public class NavigateMailFolderEventArgs
    {
        public NavigateMailFolderEventArgs(IBaseFolderMenuItem baseFolderMenuItem, TaskCompletionSource<bool> folderInitLoadAwaitTask = null)
        {
            BaseFolderMenuItem = baseFolderMenuItem;
            FolderInitLoadAwaitTask = folderInitLoadAwaitTask;
        }

        /// <summary>
        /// Base folder menu item.
        /// </summary>
        public IBaseFolderMenuItem BaseFolderMenuItem { get; set; }

        /// <summary>
        /// Completion source for waiting folder's mail initialization.
        /// </summary>
        public TaskCompletionSource<bool> FolderInitLoadAwaitTask { get; }
    }
}
