#nullable enable

using System.ComponentModel;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// A mode view model that owns the navigation menu shown while that mode is active.
/// The shell pulls <see cref="ShellMenu"/> from whatever the inner frame navigated to and
/// never builds or mutates the collections behind it.
/// </summary>
public interface IShellMenuProvider : INotifyPropertyChanged
{
    WinoApplicationMode Mode { get; }

    IDispatcher Dispatcher { get; set; }

    /// <summary>
    /// Stable menu instance for the lifetime of this provider. Null until a dispatcher has
    /// been assigned, because the menu collections marshal their own updates.
    /// </summary>
    ShellMenu? ShellMenu { get; }

    object? SelectedMenuItem { get; set; }

    /// <summary>
    /// Prepares the mode and navigates the inner shell frame to its root page.
    /// </summary>
    void ActivateShellMenu(ShellModeActivationContext activationContext);

    /// <summary>
    /// The navigation pane collapsed to, or expanded from, the icon-only strip. Entries that
    /// carry their own content instead of a navigation item icon cannot render at that width,
    /// so each mode decides which of its entries survive.
    /// </summary>
    void SetPaneCompact(bool isCompact);

    Task OnMenuItemInvokedAsync(IMenuItem? menuItem);

    Task OnMenuSelectionChangedAsync(IMenuItem? menuItem);

    Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args);

    /// <summary>
    /// Called when the mode is switched away from. Clears menu collections, drops runtime
    /// subscriptions and releases anything the navigation view could otherwise keep alive.
    /// </summary>
    void ReleaseShellMenu();
}
