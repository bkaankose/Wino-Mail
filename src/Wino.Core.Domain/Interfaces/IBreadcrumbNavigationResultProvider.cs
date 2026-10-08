using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

public interface IBreadcrumbNavigationResultProvider
{
    NavigationResult? TakeNavigationResult();
}
