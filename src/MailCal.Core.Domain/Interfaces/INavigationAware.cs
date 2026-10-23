using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Interfaces;

public interface INavigationAware
{
    void OnNavigatedTo(NavigationMode mode, object parameters);
    void OnNavigatedFrom(NavigationMode mode, object parameters);
}
