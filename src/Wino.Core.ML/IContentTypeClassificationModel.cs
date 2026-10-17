#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.ML;

public interface IContentTypeClassificationModel
{
    bool IsSupported { get; }

    /// <summary>Why <see cref="IsSupported"/> is false, or null when the model can run.</summary>
    string? UnavailableReason { get; }

    ValueTask<ContentTypeClassificationResult> ClassifyAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}
