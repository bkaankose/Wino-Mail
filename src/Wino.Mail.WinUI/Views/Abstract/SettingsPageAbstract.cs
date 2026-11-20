using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.WinUI.Views.Abstract;

public abstract class SettingsPageAbstract : BasePage<SettingsPageViewModel>
{
    protected SettingsPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
