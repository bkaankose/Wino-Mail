using Wino.Mail.WinUI.ViewModels;

namespace Wino.Mail.WinUI.Views.Abstract;

public abstract class WinoAppShellAbstract : BasePage<WinoAppShellViewModel>
{
    protected WinoAppShellAbstract()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }
}
