using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.MacOS.Services;

public sealed class MacExternalLauncher(IDispatcher dispatcher) : IExternalLauncher
{
    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "The file does not exist."));
        return OpenAsync(new Uri(Path.GetFullPath(path)), cancellationToken);
    }

    public Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http" or "mailto"))
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "Unsupported external URL scheme."));
        return OpenAsync(uri, cancellationToken);
    }

    private async Task<PlatformOperationResult> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool opened = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var nativeUrl = new NSUrl(uri.AbsoluteUri);
            opened = NSWorkspace.SharedWorkspace.OpenUrl(nativeUrl);
        });
        return new(opened ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed,
            opened ? null : "macOS declined to open the resource.");
    }
}
