using Wino.Domain.Models.Navigation;

namespace Wino.Domain.Interfaces
{
    public interface INavigationAware
    {
        void OnNavigatedTo(NavigationMode mode, object parameters);
        void OnNavigatedFrom(NavigationMode mode, object parameters);
    }
}
