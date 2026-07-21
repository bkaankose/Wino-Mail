using Windows.UI.Xaml.Navigation;
using Wino.Mail.Uwp;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public partial class MailListPageAbstract : BasePage<MailListPageViewModel>
{
    protected MailListPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
