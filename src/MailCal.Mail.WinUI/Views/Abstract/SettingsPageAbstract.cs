using Microsoft.UI.Xaml.Navigation;
using MailCal.Mail.WinUI;
using MailCal.Core.ViewModels;

namespace MailCal.Views.Abstract;

public abstract class SettingsPageAbstract : BasePage<SettingsPageViewModel>
{
    protected SettingsPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
