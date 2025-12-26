using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

public interface INavigationService
{
    bool Navigate(WinoPage page,
                         object parameter = null,
                         NavigationReferenceFrame frame = NavigationReferenceFrame.InnerShellFrame,
                         NavigationTransitionType transition = NavigationTransitionType.None);

    Type GetPageType(WinoPage winoPage);
    void GoBack();
    bool ChangeApplicationMode(WinoApplicationMode mode);
}
