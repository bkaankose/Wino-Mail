using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Contacts;

namespace Wino.Mail.WinUI.ViewModels;

public sealed class ContactsShellClient(INavigationService navigationService) : CoreBaseViewModel, IShellClient
{
    private readonly NewContactMenuItem _newContactMenuItem = new();

    public WinoApplicationMode Mode => WinoApplicationMode.Contacts;
    public MenuItemCollection? MenuItems { get; private set; }
    public object? SelectedMenuItem { get; set; }
    public bool HandlesNavigationSelection => false;

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();
        MenuItems ??= new MenuItemCollection(Dispatcher);

        if (MenuItems.Count == 0)
        {
            MenuItems.Add(_newContactMenuItem);
        }
    }

    public void Activate(ShellModeActivationContext activationContext)
    {
        OnNavigatedTo(NavigationMode.New, activationContext);

        if (MenuItems?.Count == 0)
        {
            MenuItems.Add(_newContactMenuItem);
        }

        navigationService.Navigate(WinoPage.ContactsPage, null, NavigationReferenceFrame.InnerShellFrame);
    }

    public void Deactivate()
    {
        OnNavigatedFrom(NavigationMode.New, null!);
    }

    public void PrepareForShellShutdown()
    {
        SelectedMenuItem = null;
        if (MenuItems != null)
        {
            MenuItems.Clear();
            MenuItems.Add(_newContactMenuItem);
        }
    }

    public Task HandleNavigationItemInvokedAsync(IMenuItem? menuItem)
    {
        if (menuItem is NewContactMenuItem)
        {
            WeakReferenceMessenger.Default.Send(new NewContactRequested());
        }

        return Task.CompletedTask;
    }

    public Task HandleNavigationSelectionChangedAsync(IMenuItem? menuItem) => Task.CompletedTask;
}
