using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Mail.ViewModels.Messages;

public class ActiveMailFolderChangedEvent : NavigateMailFolderEventArgs
{
    public ActiveMailFolderChangedEvent(IBaseFolderMenuItem baseFolderMenuItem,
                                        TaskCompletionSource<bool> folderInitLoadAwaitTask = null) : base(baseFolderMenuItem, folderInitLoadAwaitTask)
    {
    }
}
