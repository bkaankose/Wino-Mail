using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// URLs open through <see cref="UIApplication.OpenUrlAsync(NSUrl, UIApplicationOpenUrlOptions)"/>; files go to the
/// head's <see cref="IIOSFilePresenter"/> (the last one registered), or report Unavailable when there is none.
/// </summary>
public sealed class IOSExternalLauncher(IDispatcher dispatcher, IEnumerable<IIOSFilePresenter> filePresenters) : IExternalLauncher
{
    private readonly IIOSFilePresenter? _filePresenter = filePresenters.LastOrDefault();

    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "The file does not exist."));
        if (_filePresenter is null)
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable, "No file presenter is registered."));
        return _filePresenter.PresentFileAsync(Path.GetFullPath(path), cancellationToken);
    }

    public async Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http" or "mailto"))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, "Unsupported external URL scheme.");
        cancellationToken.ThrowIfCancellationRequested();

        Task<bool>? open = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeUrl = NSUrl.FromString(uri.AbsoluteUri);
            if (nativeUrl is not null)
                open = UIApplication.SharedApplication.OpenUrlAsync(nativeUrl, new UIApplicationOpenUrlOptions());
        });
        if (open is null) return new PlatformOperationResult(PlatformOperationStatus.Failed, "The address is not a valid URL.");

        var opened = await open.ConfigureAwait(false);
        return new(opened ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed,
            opened ? null : "iOS declined to open the resource.");
    }
}
