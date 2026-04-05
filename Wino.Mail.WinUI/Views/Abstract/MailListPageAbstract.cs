using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public partial class MailListPageAbstract : BasePage<MailListPageViewModel>
{
    protected MailListPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
