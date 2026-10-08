#nullable enable

using System.ComponentModel;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;

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

    #region Synchronization

    // The shell title bar hosts a single synchronization button for every mode. It reads
    // the members below off whichever provider is active and knows nothing about what is
    // being synchronized. Modes that synchronize something override them and raise
    // PropertyChanged for the ones that move; the defaults keep the button away from the
    // modes that have nothing to synchronize.

    /// <summary>
    /// Whether this mode has anything to synchronize at all. False collapses the title bar
    /// button entirely, which is how Settings opts out.
    /// </summary>
    bool IsSynchronizationSupported => false;

    /// <summary>
    /// Whether a synchronization can be started right now. False keeps the button visible
    /// but disabled, so a mode never has to hide it to say "not at the moment".
    /// </summary>
    bool CanSynchronize => false;

    /// <summary>
    /// Live progress for whatever this mode considers selected, read straight out of the
    /// synchronization manager rather than mirrored into the provider.
    /// </summary>
    ShellSynchronizationState SynchronizationState => ShellSynchronizationState.Idle;

    /// <summary>
    /// Label shown next to the ring while synchronizing, e.g. "Syncing Inbox". The mode
    /// owns the wording; the button never composes it.
    /// </summary>
    string SynchronizationDescription => string.Empty;

    /// <summary>
    /// Tooltip shown while the button is collapsed, e.g. "Sync calendars".
    /// </summary>
    string SynchronizationToolTip => string.Empty;

    /// <summary>
    /// Starts this mode's own synchronization.
    /// </summary>
    Task SynchronizeAsync() => Task.CompletedTask;

    #endregion
}
