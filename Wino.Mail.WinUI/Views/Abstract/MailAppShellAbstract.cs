using Wino.Mail.ViewModels;
using Wino.Mail.WinUI;

namespace Wino.Views.Abstract;

public abstract class MailAppShellAbstract : BasePage<MailAppShellViewModel>
{
    protected MailAppShellAbstract()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
    }
}
