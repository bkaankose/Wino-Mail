#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Core.Domain.Interfaces;

public interface IContentTypeDetectionService
{
    ValueTask<ContentTypeDetectionResult> DetectAsync(
        Stream content,
        string fileName,
        string? declaredMimeType,
        CancellationToken cancellationToken = default);
}
