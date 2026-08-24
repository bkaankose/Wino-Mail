#nullable enable

using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Navigation.Rules;

/// <summary>
/// A mode root that is already on screen is never navigated to again. Modes re-activate
/// their root on every activation, and rebuilding the page each time would throw away the
/// mail list, the calendar surface or the contacts list for no reason.
/// </summary>
/// <remarks>
/// Registered after the page specific rules so those get the chance to absorb the request
/// with a parameter first.
/// </remarks>
public sealed class ModeRootReentryRule : INavigationReentryRule
{
    public WinoPage Page => WinoPage.None;

    public RouteKind? AppliesTo => RouteKind.ModeRoot;

    public ReentryDecision Evaluate(NavigationContext context)
        => context.IsTargetActive ? ReentryDecision.Suppress() : ReentryDecision.Navigate();
}
