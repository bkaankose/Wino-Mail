using Microsoft.UI.Xaml.Navigation;
using MailCal.Mail.WinUI;
using MailCal.Mail.ViewModels;

namespace MailCal.Views.Abstract;

public partial class MailListPageAbstract : BasePage<MailListPageViewModel>
{
    protected MailListPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
