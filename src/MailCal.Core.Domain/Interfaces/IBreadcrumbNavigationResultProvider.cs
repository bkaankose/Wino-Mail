using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Interfaces;

public interface IBreadcrumbNavigationResultProvider
{
    NavigationResult? TakeNavigationResult();
}
