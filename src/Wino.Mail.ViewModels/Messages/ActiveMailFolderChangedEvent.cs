using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels.Messages;

public class ActiveMailFolderChangedEvent : NavigateMailFolderEventArgs
{
    public ActiveMailFolderChangedEvent(IBaseFolderMenuItem baseFolderMenuItem,
                                        TaskCompletionSource<bool> folderInitLoadAwaitTask = null) : base(baseFolderMenuItem, folderInitLoadAwaitTask)
    {
    }
}
