#nullable enable

using System;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Navigation;

/// <summary>
/// How a page participates in navigation. Drives frame selection and back stack policy.
/// </summary>
public enum RouteKind
{
    /// <summary>
    /// Root page of an application mode. Arriving at one resets the inner shell back stack.
    /// Must be cached so returning from a detail page does not rebuild it.
    /// </summary>
    ModeRoot,

    /// <summary>
    /// Drilled into from a mode root. Pushed onto the inner shell back stack.
    /// </summary>
    Detail,

    /// <summary>
    /// Lives in the rendering frame hosted by the mail list page.
    /// </summary>
    Rendering,

    /// <summary>
    /// Owns the whole window frame, outside the application shell (account setup wizard).
    /// </summary>
    Standalone,

    /// <summary>
    /// Hosted by another page's private frame (settings breadcrumb frame, welcome wizard frame).
    /// The navigation service never drives these directly; settings pages are redirected to
    /// a Settings mode activation instead.
    /// </summary>
    Hosted
}

/// <summary>
/// Declarative description of one navigable page. Replaces the page type switch and the
/// per-mode page allow-lists that used to live in the navigation service.
/// </summary>
/// <param name="Page">Logical page identifier.</param>
/// <param name="PageType">Concrete view type.</param>
/// <param name="Mode">Mode the page belongs to. Null means the page is valid in every mode.</param>
/// <param name="Frame">Frame the page is navigated into when the caller does not name one.</param>
/// <param name="Kind">Navigation role of the page.</param>
public sealed record NavigationRoute(
    WinoPage Page,
    Type PageType,
    WinoApplicationMode? Mode,
    NavigationReferenceFrame Frame,
    RouteKind Kind)
{
    public bool IsAllowedIn(WinoApplicationMode mode)
        => Kind == RouteKind.Standalone || Mode is null || Mode == mode;
}
