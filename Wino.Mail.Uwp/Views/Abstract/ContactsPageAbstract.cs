using Windows.UI.Xaml.Navigation;
using Wino.Mail.Uwp;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public abstract class ContactsPageAbstract : BasePage<ContactsPageViewModel>
{
    protected ContactsPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
