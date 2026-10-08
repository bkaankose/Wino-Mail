#nullable enable

using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Implemented by view models that may refuse a back navigation, for example because
/// they hold unsaved changes. The navigation service asks before popping the frame.
/// </summary>
public interface IConfirmBackNavigation
{
    /// <summary>
    /// Returns false to cancel the pending back navigation. Implementations may prompt.
    /// </summary>
    ValueTask<bool> CanNavigateBackAsync();
}
