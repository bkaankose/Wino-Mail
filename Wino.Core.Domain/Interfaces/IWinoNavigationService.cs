using Wino.Domain.Enums;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Navigation;

namespace Wino.Domain.Interfaces
{
    public interface IWinoNavigationService
    {
        bool Navigate(WinoPage page,
                             object parameter = null,
                             NavigationReferenceFrame frame = NavigationReferenceFrame.ShellFrame,
                             NavigationTransitionType transition = NavigationTransitionType.None);
        void NavigateCompose(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None);
        void NavigateRendering(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None);
        void NavigateRendering(MimeMessageInformation mimeMessageInformation, NavigationTransitionType transition = NavigationTransitionType.None);
        void NavigateFolder(NavigateMailFolderEventArgs args);
    }
}
