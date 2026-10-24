using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.MailItem;

public readonly record struct HoverMailActions(MailOperation Left, MailOperation Center, MailOperation Right);

public readonly record struct SwipeMailActions(MailOperation Left, MailOperation Right);

/// <summary>
/// Resolves the actions a mail row offers: the app-wide choice unless the row's account
/// carries its own. Both heads and the settings page go through here so they never disagree.
/// </summary>
public static class MailListActionPreferences
{
    public static HoverMailActions GetDefaultHoverActions(IPreferencesService preferences)
        => new(preferences.LeftHoverAction, preferences.CenterHoverAction, preferences.RightHoverAction);

    public static SwipeMailActions GetDefaultSwipeActions(IPreferencesService preferences)
        => new(preferences.LeftSwipeOperation, preferences.RightSwipeOperation);

    public static HoverMailActions ResolveHoverActions(IPreferencesService preferences, MailAccountPreferences? accountPreferences)
        => accountPreferences is { HasCustomHoverActions: true }
            ? GetCustomHoverActions(accountPreferences)
            : GetDefaultHoverActions(preferences);

    public static SwipeMailActions ResolveSwipeActions(IPreferencesService preferences, MailAccountPreferences? accountPreferences)
        => accountPreferences is { HasCustomSwipeActions: true }
            ? GetCustomSwipeActions(accountPreferences)
            : GetDefaultSwipeActions(preferences);

    public static HoverMailActions GetCustomHoverActions(MailAccountPreferences accountPreferences)
        => new(accountPreferences.CustomLeftHoverAction, accountPreferences.CustomCenterHoverAction, accountPreferences.CustomRightHoverAction);

    public static SwipeMailActions GetCustomSwipeActions(MailAccountPreferences accountPreferences)
        => new(accountPreferences.CustomLeftSwipeOperation, accountPreferences.CustomRightSwipeOperation);

    /// <summary>
    /// Stores the account's hover actions. Values equal to the app-wide ones mean the account
    /// follows the defaults again, so the override is dropped instead of kept as a copy.
    /// </summary>
    public static void SetCustomHoverActions(MailAccountPreferences accountPreferences, HoverMailActions actions, HoverMailActions defaults)
    {
        accountPreferences.CustomLeftHoverAction = actions.Left;
        accountPreferences.CustomCenterHoverAction = actions.Center;
        accountPreferences.CustomRightHoverAction = actions.Right;
        accountPreferences.HasCustomHoverActions = actions != defaults;
    }

    public static void SetCustomSwipeActions(MailAccountPreferences accountPreferences, SwipeMailActions actions, SwipeMailActions defaults)
    {
        accountPreferences.CustomLeftSwipeOperation = actions.Left;
        accountPreferences.CustomRightSwipeOperation = actions.Right;
        accountPreferences.HasCustomSwipeActions = actions != defaults;
    }

    public static bool HasCustomHoverActions(MailAccountPreferences? accountPreferences, HoverMailActions defaults)
        => accountPreferences is { HasCustomHoverActions: true } && GetCustomHoverActions(accountPreferences) != defaults;

    public static bool HasCustomSwipeActions(MailAccountPreferences? accountPreferences, SwipeMailActions defaults)
        => accountPreferences is { HasCustomSwipeActions: true } && GetCustomSwipeActions(accountPreferences) != defaults;
}
