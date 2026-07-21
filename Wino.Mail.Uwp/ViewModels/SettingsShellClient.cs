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

namespace Wino.Mail.Uwp.ViewModels;

public partial class SettingsShellClient(INavigationService navigationService) :
    CoreBaseViewModel,
    IShellClient,
    IRecipient<ActiveSettingsPageChanged>,
    IRecipient<LanguageChanged>
{
    private bool _hasRegisteredPersistentRecipients;
    private MenuItemCollection? _menuItems;

    public WinoApplicationMode Mode => WinoApplicationMode.Settings;
    public MenuItemCollection? MenuItems
    {
        get => _menuItems;
        private set => SetProperty(ref _menuItems, value);
    }

    [ObservableProperty]
    public partial object? SelectedMenuItem { get; set; } = null;

    public bool HandlesNavigationSelection => true;

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();
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

    public void PrepareForShellShutdown()
    {
        if (_hasRegisteredPersistentRecipients)
        {
            UnregisterRecipients();
            _hasRegisteredPersistentRecipients = false;
        }

        SelectedMenuItem = null;
        MenuItems = null;
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
        _ = ExecuteUIThread(() => SetSelectedRootPage(message.RootPage));
    }

    public void Receive(LanguageChanged message)
    {
        _ = ExecuteUIThread(() =>
        {
            var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;
            RebuildMenuItems();
            SetSelectedRootPage(selectedPage);
        });
    }

    private void RebuildMenuItems()
    {
        var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;
        var replacement = new MenuItemCollection(Dispatcher);

        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator)
            {
                replacement.Add(new SettingsShellSectionMenuItem(item.Title, item.Glyph));
                continue;
            }

            if (!item.PageType.HasValue)
                continue;

            replacement.Add(new SettingsShellPageMenuItem(item.PageType.Value, item.Title, item.Description, item.Glyph));
        }

        // Replacing the source atomically avoids the invalid -1 collection index
        // produced by modern .NET's UWP projection for both Reset and Remove events.
        MenuItems = replacement;
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
