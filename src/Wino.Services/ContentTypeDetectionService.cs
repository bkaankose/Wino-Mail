#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.ML;

namespace Wino.Services;

public sealed class ContentTypeDetectionService(
    IContentTypeClassificationModel classificationModel,
    IWinoLogger logger) : IContentTypeDetectionService
{
    private static readonly HashSet<string> GenericMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream",
        "binary/octet-stream",
        "application/unknown",
        "unknown/unknown"
    };

    public async ValueTask<ContentTypeDetectionResult> DetectAsync(
        Stream content,
        string fileName,
        string? declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!classificationModel.IsSupported)
            return ContentTypeDetectionResult.Unavailable;

        try
        {
            var classification = await classificationModel.ClassifyAsync(content, cancellationToken).ConfigureAwait(false);
            var status = classification.Status switch
            {
                ContentTypeClassificationStatus.Detected => ContentTypeDetectionStatus.Detected,
                ContentTypeClassificationStatus.LowConfidence => ContentTypeDetectionStatus.LowConfidence,
                ContentTypeClassificationStatus.Unavailable => ContentTypeDetectionStatus.Unavailable,
                _ => ContentTypeDetectionStatus.Failed
            };

            var extension = Path.GetExtension(fileName).TrimStart('.');
            var hasExtensionMismatch = status == ContentTypeDetectionStatus.Detected &&
                !string.IsNullOrWhiteSpace(extension) &&
                classification.CompatibleExtensions.Count > 0 &&
                !classification.CompatibleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            var hasMimeMismatch = status == ContentTypeDetectionStatus.Detected &&
                IsSpecificMimeType(declaredMimeType) &&
                IsSpecificMimeType(classification.MimeType) &&
                !declaredMimeType!.Equals(classification.MimeType, StringComparison.OrdinalIgnoreCase);

            return new ContentTypeDetectionResult(
                status,
                classification.Label,
                classification.MimeType,
                classification.Description,
                classification.Group,
                classification.Confidence,
                classification.CompatibleExtensions,
                hasExtensionMismatch,
                hasMimeMismatch,
                classification.Provider,
                classification.ModelVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.CaptureException(ex, "AttachmentContentTypeDetection");
            return new ContentTypeDetectionResult(ContentTypeDetectionStatus.Failed);
        }
    }

    private static bool IsSpecificMimeType(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType) && !GenericMimeTypes.Contains(mimeType);
}
