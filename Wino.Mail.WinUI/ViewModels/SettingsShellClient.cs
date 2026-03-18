using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;

namespace Wino.Mail.WinUI.ViewModels;

public partial class SettingsShellClient(INavigationService navigationService) :
    CoreBaseViewModel,
    IShellClient,
    IRecipient<ActiveSettingsPageChanged>,
    IRecipient<LanguageChanged>
{
    private bool _hasRegisteredPersistentRecipients;

    public WinoApplicationMode Mode => WinoApplicationMode.Settings;
    public MenuItemCollection? MenuItems { get; private set; }

    [ObservableProperty]
    public partial object? SelectedMenuItem { get; set; } = null;

    public bool HandlesNavigationSelection => true;

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();
        MenuItems ??= new MenuItemCollection(Dispatcher);
        RebuildMenuItems();
    }

    public void Activate(ShellModeActivationContext activationContext)
    {
        if (!_hasRegisteredPersistentRecipients)
        {
            RegisterRecipients();
            _hasRegisteredPersistentRecipients = true;
        }

        RebuildMenuItems();

        var targetPage = activationContext.Parameter as WinoPage? ?? WinoPage.SettingOptionsPage;
        SetSelectedRootPage(SettingsNavigationInfoProvider.GetRootPage(targetPage));

        navigationService.Navigate(WinoPage.SettingsPage, targetPage, NavigationReferenceFrame.InnerShellFrame);
    }

    public void Deactivate()
    {
    }

    public Task HandleNavigationItemInvokedAsync(IMenuItem? menuItem)
    {
        if (menuItem is not SettingsShellPageMenuItem settingsMenuItem)
            return Task.CompletedTask;

        var currentPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType;
        if (currentPage == settingsMenuItem.PageType && settingsMenuItem.PageType != WinoPage.SettingOptionsPage)
            return Task.CompletedTask;

        SetSelectedRootPage(settingsMenuItem.PageType);
        Messenger.Send(new SettingsRootNavigationRequested(settingsMenuItem.PageType));
        return Task.CompletedTask;
    }

    public Task HandleNavigationSelectionChangedAsync(IMenuItem? menuItem)
    {
        if (menuItem is not SettingsShellPageMenuItem settingsMenuItem)
            return Task.CompletedTask;

        if ((SelectedMenuItem as SettingsShellPageMenuItem)?.PageType == settingsMenuItem.PageType)
            return Task.CompletedTask;

        SetSelectedRootPage(settingsMenuItem.PageType);
        Messenger.Send(new SettingsRootNavigationRequested(settingsMenuItem.PageType));
        return Task.CompletedTask;
    }

    public override Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args) => Task.CompletedTask;

    public void Receive(ActiveSettingsPageChanged message)
    {
        SetSelectedRootPage(message.RootPage);
    }

    public void Receive(LanguageChanged message)
    {
        var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;
        RebuildMenuItems();
        SetSelectedRootPage(selectedPage);
    }

    private void RebuildMenuItems()
    {
        if (MenuItems == null)
            return;

        var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;

        MenuItems.Clear();

        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator)
            {
                MenuItems.Add(new SettingsShellSectionMenuItem(item.Title, item.Glyph));
                continue;
            }

            if (!item.PageType.HasValue)
                continue;

            MenuItems.Add(new SettingsShellPageMenuItem(item.PageType.Value, item.Title, item.Description, item.Glyph));
        }

        SetSelectedRootPage(selectedPage);
    }

    private void SetSelectedRootPage(WinoPage pageType)
    {
        if (MenuItems == null)
            return;

        var rootPage = SettingsNavigationInfoProvider.GetRootPage(pageType);
        var selectedItem = MenuItems.OfType<SettingsShellPageMenuItem>()
            .FirstOrDefault(item => item.PageType == rootPage);

        if (ReferenceEquals(SelectedMenuItem, selectedItem))
            return;

        SelectedMenuItem = selectedItem;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<ActiveSettingsPageChanged>(this);
        Messenger.Register<LanguageChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();
        Messenger.Unregister<ActiveSettingsPageChanged>(this);
        Messenger.Unregister<LanguageChanged>(this);
    }
}
