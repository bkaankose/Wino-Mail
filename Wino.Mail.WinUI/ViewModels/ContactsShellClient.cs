using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;

namespace Wino.Mail.WinUI.ViewModels;

public sealed class ContactsShellClient(INavigationService navigationService) : CoreBaseViewModel, IShellClient
{
    public WinoApplicationMode Mode => WinoApplicationMode.Contacts;
    public MenuItemCollection? MenuItems { get; private set; }
    public object? SelectedMenuItem { get; set; }
    public bool HandlesNavigationSelection => false;

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();
        MenuItems ??= new MenuItemCollection(Dispatcher);
    }

    public void Activate(ShellModeActivationContext activationContext)
    {
        OnNavigatedTo(NavigationMode.New, activationContext);
        navigationService.Navigate(WinoPage.ContactsPage, null, NavigationReferenceFrame.InnerShellFrame);
    }

    public void Deactivate()
    {
        OnNavigatedFrom(NavigationMode.New, null!);
    }

    public Task HandleNavigationItemInvokedAsync(IMenuItem? menuItem) => Task.CompletedTask;

    public Task HandleNavigationSelectionChangedAsync(IMenuItem? menuItem) => Task.CompletedTask;
}
