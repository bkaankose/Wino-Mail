using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Navigation;

/// <summary>
/// When back navigation is requested for breadcrumb pages.
/// </summary>
/// <param name="SlideEffect">The slide animation effect to use during navigation.</param>
public record BackBreadcrumNavigationRequested(NavigationTransitionEffect SlideEffect = NavigationTransitionEffect.FromRight);
