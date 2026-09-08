#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Attachments;

public sealed record ContentTypeDetectionResult(
    ContentTypeDetectionStatus Status,
    string? Label = null,
    string? MimeType = null,
    string? Description = null,
    string? Group = null,
    double Confidence = 0,
    IReadOnlyList<string>? Extensions = null,
    bool IsExtensionMismatch = false,
    bool IsDeclaredMimeMismatch = false,
    string? Provider = null,
    string? ModelVersion = null)
{
    public static ContentTypeDetectionResult NotInspected { get; } = new(ContentTypeDetectionStatus.NotInspected);
    public static ContentTypeDetectionResult Unavailable { get; } = new(ContentTypeDetectionStatus.Unavailable);

    public bool RequiresOpenConfirmation =>
        Status == ContentTypeDetectionStatus.Detected && IsExtensionMismatch;

    public IReadOnlyList<string> CompatibleExtensions => Extensions ?? Array.Empty<string>();
}
