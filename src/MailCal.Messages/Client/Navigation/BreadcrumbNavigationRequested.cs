using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.Client.Navigation;

/// <summary>
/// When Breadcrumb control navigation requested.
/// </summary>
/// <param name="PageTitle">Title to display for the page.</param>
/// <param name="PageType">Enum equilavent of the page to navigate.</param>
/// <param name="Parameter">Additional parameters to the page.</param>
public record BreadcrumbNavigationRequested(string PageTitle, MailCalPage PageType, object Parameter = null);
