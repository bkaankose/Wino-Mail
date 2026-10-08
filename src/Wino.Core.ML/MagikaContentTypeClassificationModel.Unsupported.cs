#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.ML;

public sealed class MagikaContentTypeClassificationModel : IContentTypeClassificationModel, IDisposable
{
    public bool IsSupported => false;

    public MagikaContentTypeClassificationModel()
    {
    }

    internal MagikaContentTypeClassificationModel(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
    }

    public ValueTask<ContentTypeClassificationResult> ClassifyAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ContentTypeClassificationResult.Unavailable);
    }

    internal static void ThrowIfRuntimeRequested() =>
        throw new PlatformNotSupportedException("Local ML inference is supported only on x64 and ARM64.");

    public void Dispose()
    {
    }
}
