using MailCal.Shell.ViewModels;

namespace MailCal.Mail.WinUI.Views.Abstract;

public abstract class MailCalAppShellAbstract : BasePage<MailCalAppShellViewModel>
{
    protected MailCalAppShellAbstract()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }
}
