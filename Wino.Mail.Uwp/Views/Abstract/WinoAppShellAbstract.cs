using Wino.Mail.Uwp.ViewModels;

namespace Wino.Mail.Uwp.Views.Abstract;

public abstract class WinoAppShellAbstract : BasePage<WinoAppShellViewModel>
{
    protected WinoAppShellAbstract()
    {
        NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }
}
