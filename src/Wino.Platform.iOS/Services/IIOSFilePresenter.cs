using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// Shows a local file to the user. iOS has no "open with the default app" call for files inside the sandbox, so the
/// app head implements this with QuickLook (<c>QLPreviewController</c>) or <c>UIDocumentInteractionController</c>
/// and registers it as a singleton. Without a registration <see cref="IOSExternalLauncher.LaunchFileAsync"/>
/// reports <see cref="Wino.Core.Domain.Enums.PlatformOperationStatus.Unavailable"/>.
/// </summary>
public interface IIOSFilePresenter
{
    /// <summary>
    /// Presents <paramref name="path"/> (an existing full path). Called on any thread; the implementation moves to
    /// the main thread. Returns Succeeded once the preview or menu is on screen, Cancelled when the user dismissed
    /// an options menu without choosing, Unavailable when no view controller can present, Failed otherwise.
    /// </summary>
    Task<PlatformOperationResult> PresentFileAsync(string path, CancellationToken cancellationToken);
}
