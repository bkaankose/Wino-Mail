using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces
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
