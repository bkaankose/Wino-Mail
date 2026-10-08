#nullable enable

using System.Threading.Tasks;

namespace Wino.Mail.WinUI.Interfaces;

/// <summary>
/// A page that can absorb a navigation request aimed at itself instead of being replaced.
/// Lets the navigation service reuse an expensive page (and its WebView2) without naming
/// any concrete page type.
/// </summary>
public interface IReentryTarget
{
    /// <summary>
    /// Returns false when the page cannot handle this particular parameter, in which case
    /// the navigation service falls back to a real navigation.
    /// </summary>
    bool CanReenter(object parameter);

    Task ReenterAsync(object parameter);
}
