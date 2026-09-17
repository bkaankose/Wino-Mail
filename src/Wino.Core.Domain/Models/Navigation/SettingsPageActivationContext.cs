#nullable enable

using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// Opens Settings on a page. With a <paramref name="Route"/> the page is a nested one and the
/// route supplies its breadcrumb parents, the same way a settings search result does.
/// </summary>
public sealed record SettingsPageActivationContext(WinoPage TargetPage, object? PageParameter = null, SettingsNavigationRoute? Route = null);
