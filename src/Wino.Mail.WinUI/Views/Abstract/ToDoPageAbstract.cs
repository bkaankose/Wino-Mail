using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.WinUI;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public abstract class ToDoPageAbstract : BasePage<ToDoPageViewModel>
{
    protected ToDoPageAbstract() => NavigationCacheMode = NavigationCacheMode.Required;
}
