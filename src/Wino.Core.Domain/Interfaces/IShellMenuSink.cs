#nullable enable

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The shell surface that hosts a navigation menu. The navigation service hands it the
/// provider belonging to whatever the inner frame just navigated to.
/// </summary>
public interface IShellMenuSink
{
    /// <summary>
    /// Publishes the menu of the newly navigated mode root. Passing null clears the menu,
    /// which is done before a mode switch so the navigation view releases its containers.
    /// </summary>
    void SetShellMenu(IShellMenuProvider? provider);
}
