using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public abstract class ContactsPageAbstract : BasePage<ContactsPageViewModel>
{
    protected ContactsPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
