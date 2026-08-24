#nullable enable

using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Interfaces;

/// <summary>
/// A page hosted by the inner shell frame that owns navigation inside itself, such as the
/// settings breadcrumb frame or the mail list's narrow reading pane. Back navigation is
/// offered to the active page first, so the navigation service never inspects page types.
/// </summary>
public interface IInnerNavigationHost
{
    bool CanNavigateBack { get; }

    /// <summary>
    /// Returns true when the request was consumed internally. False lets the navigation
    /// service fall back to the inner shell frame back stack.
    /// </summary>
    Task<bool> NavigateBackAsync(NavigationTransitionEffect effect);
}
