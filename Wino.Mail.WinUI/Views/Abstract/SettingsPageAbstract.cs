using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Core.ViewModels;

namespace Wino.Views.Abstract;

public abstract class SettingsPageAbstract : BasePage<SettingsPageViewModel>
{
    protected SettingsPageAbstract()
    {
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }
}
