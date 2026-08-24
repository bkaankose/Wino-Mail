#nullable enable

using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Navigation;

/// <summary>
/// What the navigation service should do instead of a plain frame navigation.
/// </summary>
public enum ReentryAction
{
    /// <summary>
    /// No special handling; navigate the frame normally.
    /// </summary>
    Navigate,

    /// <summary>
    /// The request is a no-op. Report success without touching the frame.
    /// </summary>
    Suppress,

    /// <summary>
    /// The page already on screen absorbs the request. Run the callback instead of navigating.
    /// </summary>
    HandleInPlace,

    /// <summary>
    /// The target is the previous back stack entry. Go back to reuse it, then run the callback.
    /// </summary>
    ReuseBackStackEntry
}

/// <summary>
/// Outcome of a re-entry rule. The callback is strongly typed at the rule, so message
/// dispatch stays generic-free at the navigation service.
/// </summary>
public readonly record struct ReentryDecision(ReentryAction Action, Func<Task>? Callback = null)
{
    public static ReentryDecision Navigate() => new(ReentryAction.Navigate);

    public static ReentryDecision Suppress() => new(ReentryAction.Suppress);

    public static ReentryDecision HandleInPlace(Func<Task> callback) => new(ReentryAction.HandleInPlace, callback);

    public static ReentryDecision HandleInPlace(Action callback)
        => new(ReentryAction.HandleInPlace, () => { callback(); return Task.CompletedTask; });

    public static ReentryDecision ReuseBackStackEntry(Action callback)
        => new(ReentryAction.ReuseBackStackEntry, () => { callback(); return Task.CompletedTask; });
}

/// <summary>
/// A declared exception to "navigating somewhere always moves the frame". Replaces the
/// nested conditionals that used to guard re-navigation inside the navigation service.
/// </summary>
public interface INavigationReentryRule
{
    /// <summary>
    /// Page this rule guards. <see cref="WinoPage.None"/> means the rule is evaluated for
    /// every page whose route matches <see cref="AppliesTo"/>.
    /// </summary>
    WinoPage Page { get; }

    /// <summary>
    /// Route kind this rule applies to. Only consulted when <see cref="Page"/> is None.
    /// </summary>
    RouteKind? AppliesTo => null;

    ReentryDecision Evaluate(NavigationContext context);
}
