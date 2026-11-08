using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Core.Domain.Interfaces;

public interface IExternalLauncher
{
    Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>Shows a local folder in the platform's file manager. Unavailable where the platform has none.</summary>
    Task<PlatformOperationResult> LaunchFolderAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));
}
