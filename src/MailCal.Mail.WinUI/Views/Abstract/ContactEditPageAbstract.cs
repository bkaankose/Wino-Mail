using Microsoft.UI.Xaml.Navigation;
using MailCal.Mail.WinUI;
using MailCal.Mail.ViewModels;

namespace MailCal.Views.Abstract;

public abstract class ContactEditPageAbstract : BasePage<ContactEditPageViewModel>
{
    protected ContactEditPageAbstract() => NavigationCacheMode = NavigationCacheMode.Disabled;
}
