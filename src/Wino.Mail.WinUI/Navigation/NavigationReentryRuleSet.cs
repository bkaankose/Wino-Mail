#nullable enable

using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Navigation;

/// <summary>
/// Evaluates the registered re-entry rules in registration order and returns the first
/// decision that is not a plain navigation. Page specific rules must be registered before
/// the broader route-kind rules.
/// </summary>
public sealed class NavigationReentryRuleSet(IEnumerable<INavigationReentryRule> rules)
{
    private readonly INavigationReentryRule[] _rules = rules.ToArray();

    public ReentryDecision Evaluate(NavigationContext context)
    {
        foreach (var rule in _rules)
        {
            if (!Applies(rule, context))
                continue;

            var decision = rule.Evaluate(context);

            if (decision.Action != ReentryAction.Navigate)
                return decision;
        }

        return ReentryDecision.Navigate();
    }

    private static bool Applies(INavigationReentryRule rule, NavigationContext context)
        => rule.Page == WinoPage.None
            ? rule.AppliesTo == context.Route.Kind
            : rule.Page == context.Page;
}
