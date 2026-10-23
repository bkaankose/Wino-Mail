using MailCal.Core.Domain.Enums;

using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Messaging.Client.Navigation;

/// <summary>
/// When back navigation is requested for breadcrumb pages.
/// </summary>
/// <param name="SlideEffect">The slide animation effect to use during navigation.</param>
public record BackBreadcrumNavigationRequested(
    NavigationTransitionEffect SlideEffect = NavigationTransitionEffect.FromRight,
    NavigationResult? Result = null);
