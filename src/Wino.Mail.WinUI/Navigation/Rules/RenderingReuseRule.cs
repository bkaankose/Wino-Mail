#nullable enable

using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Navigation.Rules;

/// <summary>
/// Reading pane pages are expensive (each owns a WebView2). When the rendering frame already
/// shows the requested page type, the page absorbs the new parameter instead of being
/// replaced. Navigating a page to itself with nothing new is a no-op.
/// </summary>
public sealed class RenderingReuseRule : INavigationReentryRule
{
    public WinoPage Page => WinoPage.None;

    public RouteKind? AppliesTo => RouteKind.Rendering;

    public ReentryDecision Evaluate(NavigationContext context)
    {
        if (!context.IsTargetActive)
            return ReentryDecision.Navigate();

        if (context.Parameter is not null &&
            context.CurrentContent is IReentryTarget reentryTarget &&
            reentryTarget.CanReenter(context.Parameter))
        {
            return ReentryDecision.HandleInPlace(() => reentryTarget.ReenterAsync(context.Parameter));
        }

        // Same page, nothing new to show. Covers the repeated idle-state navigations the
        // mail list issues whenever a selection is cleared.
        return context.Parameter is null ? ReentryDecision.Suppress() : ReentryDecision.Navigate();
    }
}
