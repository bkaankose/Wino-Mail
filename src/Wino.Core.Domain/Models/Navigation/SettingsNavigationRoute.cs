#nullable enable

using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Navigation;

public sealed record SettingsNavigationRouteStep(string PageTitle, WinoPage PageType, object? Parameter = null);

public sealed record SettingsNavigationRoute(IReadOnlyList<SettingsNavigationRouteStep> Steps)
{
    public SettingsNavigationRouteStep Destination
        => Steps.Count > 0
            ? Steps[^1]
            : throw new InvalidOperationException("A settings navigation route must contain at least one step.");
}

public enum AccountDetailsTab
{
    General,
    Mail,
    Calendar
}

public sealed record AccountDetailsNavigationContext(Guid AccountId, AccountDetailsTab SelectedTab);
