using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;

namespace Wino.Core.ViewModels;

/// <summary>
/// Owns the settings navigation pane. Settings keeps its own breadcrumb frame inside
/// <c>SettingsPage</c>, so this provider only tracks which root section is highlighted.
/// </summary>
public partial class SettingsMenuProvider(INavigationService navigationService) :
    CoreBaseViewModel,
    IShellMenuProvider,
    IRecipient<ActiveSettingsPageChanged>,
    IRecipient<LanguageChanged>
{
    private bool _hasRegisteredPersistentRecipients;
    private ShellMenu _shellMenu;
    private object _selectedMenuItem;
    private bool _isPreparedForShellShutdown;

    public WinoApplicationMode Mode => WinoApplicationMode.Settings;

    public ShellMenu ShellMenu => _shellMenu;

    public object SelectedMenuItem
    {
        get => _selectedMenuItem;
        set => SetProperty(ref _selectedMenuItem, value);
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        _isPreparedForShellShutdown = false;
        _shellMenu = new ShellMenu
        {
            Items = new MenuItemCollection(Dispatcher),
            HandlesSelection = true
        };

        RebuildMenuItems();
    }

    /// <summary>
    /// Every settings entry renders through the navigation item icon slot, so the collapsed
    /// pane shows them all correctly and nothing has to be dropped.
    /// </summary>
    public void SetPaneCompact(bool isCompact) { }

    public void ActivateShellMenu(ShellModeActivationContext activationContext)
    {
        if (!_hasRegisteredPersistentRecipients)
        {
            RegisterRecipients();
            _hasRegisteredPersistentRecipients = true;
        }

        RebuildMenuItems();

        var settingsActivationContext = activationContext?.Parameter as SettingsPageActivationContext;
        var targetPage = settingsActivationContext?.TargetPage
                         ?? activationContext?.Parameter as WinoPage?
                         ?? WinoPage.SettingOptionsPage;

        SetSelectedRootPage(SettingsNavigationInfoProvider.GetRootPage(targetPage));

        object navigationParameter = settingsActivationContext is not null
            ? settingsActivationContext
            : targetPage;

        navigationService.Navigate(WinoPage.SettingsPage, navigationParameter);
    }

    /// <summary>
    /// Mode switch. The sections are rebuilt on the next activation anyway, so nothing is
    /// torn down here beyond letting the shell drop its item containers.
    /// </summary>
    public void ReleaseShellMenu() { }

    public void PrepareForShellShutdown()
    {
        if (_isPreparedForShellShutdown)
            return;

        _isPreparedForShellShutdown = true;

        if (_hasRegisteredPersistentRecipients)
        {
            UnregisterRecipients();
            _hasRegisteredPersistentRecipients = false;
        }

        SelectedMenuItem = null;
        _shellMenu?.Items.Clear();
        _shellMenu = null;
    }

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
    {
        if (menuItem is not SettingsShellPageMenuItem settingsMenuItem)
            return Task.CompletedTask;

        var currentPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType;

        // Re-invoking the section already open is only meaningful for the home page, which
        // acts as "go back to the top" for the breadcrumb frame.
        if (currentPage == settingsMenuItem.PageType && settingsMenuItem.PageType != WinoPage.SettingOptionsPage)
            return Task.CompletedTask;

        SetSelectedRootPage(settingsMenuItem.PageType);
        Messenger.Send(new SettingsRootNavigationRequested(settingsMenuItem.PageType));
        return Task.CompletedTask;
    }

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem)
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

    public void Receive(ActiveSettingsPageChanged message) => SetSelectedRootPage(message.RootPage);

    public void Receive(LanguageChanged message)
    {
        var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;

        RebuildMenuItems();
        SetSelectedRootPage(selectedPage);
    }

    private void RebuildMenuItems()
    {
        if (_shellMenu is null)
            return;

        var selectedPage = (SelectedMenuItem as SettingsShellPageMenuItem)?.PageType ?? WinoPage.SettingOptionsPage;

        // Expansion is pane state and survives a rebuild, which happens on every activation and
        // on language change. Capture it before the items are replaced.
        var expandedGroups = _shellMenu.Items
            .OfType<SettingsShellGroupMenuItem>()
            .Where(group => group.IsExpanded)
            .Select(group => group.Title)
            .ToHashSet(StringComparer.Ordinal);

        _shellMenu.Items.Clear();

        foreach (var node in SettingsNavigationInfoProvider.GetPaneNodes())
        {
            if (!node.IsGroup)
            {
                _shellMenu.Items.Add(CreatePageMenuItem(node.Item));
                continue;
            }

            var group = new SettingsShellGroupMenuItem(node.Title, node.Glyph)
            {
                IsExpanded = expandedGroups.Contains(node.Title)
            };

            foreach (var child in node.Children)
            {
                group.SubMenuItems.Add(CreatePageMenuItem(child));
            }

            _shellMenu.Items.Add(group);
        }

        SetSelectedRootPage(selectedPage);
    }

    private static SettingsShellPageMenuItem CreatePageMenuItem(SettingsNavigationItemInfo item)
        => new(item.PageType.Value, item.Title, item.Description, item.Glyph);

    private void SetSelectedRootPage(WinoPage pageType)
    {
        if (_shellMenu is null)
            return;

        var rootPage = SettingsNavigationInfoProvider.GetRootPage(pageType);
        var selectedItem = EnumerateAllPageMenuItems().FirstOrDefault(item => item.PageType == rootPage);

        // Deep links and settings search can land inside a collapsed group, which would otherwise
        // leave the pane showing no selection at all.
        ExpandGroupContaining(selectedItem);

        if (ReferenceEquals(SelectedMenuItem, selectedItem))
            return;

        SelectedMenuItem = selectedItem;
    }

    private IEnumerable<SettingsShellPageMenuItem> EnumerateAllPageMenuItems()
    {
        foreach (var item in _shellMenu.Items)
        {
            if (item is SettingsShellPageMenuItem pageMenuItem)
            {
                yield return pageMenuItem;
                continue;
            }

            if (item is not SettingsShellGroupMenuItem group)
                continue;

            foreach (var child in group.SubMenuItems)
            {
                yield return child;
            }
        }
    }

    private void ExpandGroupContaining(SettingsShellPageMenuItem menuItem)
    {
        if (menuItem is null)
            return;

        var owningGroup = _shellMenu.Items
            .OfType<SettingsShellGroupMenuItem>()
            .FirstOrDefault(group => group.SubMenuItems.Contains(menuItem));

        if (owningGroup is { IsExpanded: false })
        {
            owningGroup.IsExpanded = true;
        }
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
