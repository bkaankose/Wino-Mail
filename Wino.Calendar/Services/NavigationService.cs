using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Calendar.Services
{
    public class NavigationService : INavigationService
    {
        public Type GetPageType(WinoPage winoPage)
        {
            throw new NotImplementedException();
        }

        public bool Navigate(WinoPage page, object parameter = null, NavigationReferenceFrame frame = NavigationReferenceFrame.ShellFrame, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            throw new NotImplementedException();
        }

        public void NavigateCompose(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            throw new NotImplementedException();
        }

        public void NavigateFolder(NavigateMailFolderEventArgs args)
        {
            throw new NotImplementedException();
        }

        public void NavigateRendering(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            throw new NotImplementedException();
        }

        public void NavigateRendering(MimeMessageInformation mimeMessageInformation, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            throw new NotImplementedException();
        }
    }
}
