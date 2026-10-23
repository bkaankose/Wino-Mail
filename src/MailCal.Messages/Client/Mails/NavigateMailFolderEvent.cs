using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Messaging.Client.Mails;

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
