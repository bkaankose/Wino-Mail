#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Attachments;

namespace MailCal.Services;

/// <summary>
/// Content sniffing with a local ML model was removed from this build. Attachment handling falls
/// back to the declared MIME type and the file extension.
/// </summary>
public sealed class ContentTypeDetectionService : IContentTypeDetectionService
{
    public ValueTask<ContentTypeDetectionResult> DetectAsync(
        Stream content,
        string fileName,
        string? declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        return ValueTask.FromResult(ContentTypeDetectionResult.Unavailable);
    }
}
