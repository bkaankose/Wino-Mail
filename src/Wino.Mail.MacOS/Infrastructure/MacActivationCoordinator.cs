using Foundation;
using Wino.Core.Activation;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Turns AppKit activations (mailto, wino://, webcal(s), files opened from Finder, dropped on the Dock icon
/// or sent from the Services menu) into <see cref="ActivationItem"/>s for the shared <see cref="ActivationRouter"/>,
/// which holds the routing rules. Files carry their security scope (<see cref="SecurityScopedFile"/>); an .eml
/// opens in its own reader window through <see cref="AppKitApplicationShellHost"/>. AppDelegate queues URLs until
/// services are ready; the router serializes handling.
/// </summary>
public sealed class MacActivationCoordinator(ActivationRouter router)
{
    /// <summary>Handles URLs in the order the OS delivered them. Files of one kind are imported together.</summary>
    public Task HandleAsync(IReadOnlyList<NSUrl> urls)
        => router.HandleAsync(urls.Select(url => new ActivationItem(Classify(url), url.AbsoluteString ?? string.Empty, url.IsFileUrl ? new SecurityScopedFile(url) : null)).ToArray());

    /// <summary>Files from the Services menu: every file is an attachment, whatever its type.</summary>
    public Task AttachFilesAsync(IReadOnlyList<NSUrl> files)
        => router.AttachFilesAsync(files.Select(file => (IActivationFile)new SecurityScopedFile(file)).ToArray());

    public static ActivationUriKind Classify(NSUrl url)
        => url.IsFileUrl ? ActivationUriClassifier.ClassifyFilePath(url.Path) : ActivationUriClassifier.Classify(url.AbsoluteString);

    /// <summary>A Finder-provided file, read inside its security scope.</summary>
    private sealed class SecurityScopedFile(NSUrl url) : IActivationFile
    {
        public string? Path => url.Path;

        public IDisposable BeginAccess() => new Scope(url, url.StartAccessingSecurityScopedResource());

        private sealed class Scope(NSUrl url, bool started) : IDisposable
        {
            private bool _started = started;

            public void Dispose()
            {
                if (!_started) return;
                _started = false;
                url.StopAccessingSecurityScopedResource();
            }
        }
    }
}
