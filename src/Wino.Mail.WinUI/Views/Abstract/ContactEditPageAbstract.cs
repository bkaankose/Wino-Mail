using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public abstract class ContactEditPageAbstract : BasePage<ContactEditPageViewModel>
{
    protected ContactEditPageAbstract() => NavigationCacheMode = NavigationCacheMode.Disabled;
}
