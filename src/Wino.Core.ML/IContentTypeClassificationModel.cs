#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.ML;

public interface IContentTypeClassificationModel
{
    bool IsSupported { get; }

    ValueTask<ContentTypeClassificationResult> ClassifyAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}
