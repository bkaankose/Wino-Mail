using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

public interface INavigationAware
{
    void OnNavigatedTo(NavigationMode mode, object parameters);
    void OnNavigatedFrom(NavigationMode mode, object parameters);
}
