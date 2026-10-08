#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.ML;

public sealed record ContentTypeClassificationResult(
    ContentTypeClassificationStatus Status,
    string? Label = null,
    string? RawLabel = null,
    string? MimeType = null,
    string? Description = null,
    string? Group = null,
    double Confidence = 0,
    IReadOnlyList<string>? Extensions = null,
    string Provider = "Magika",
    string ModelVersion = "standard_v3_3")
{
    public static ContentTypeClassificationResult Unavailable { get; } =
        new(ContentTypeClassificationStatus.Unavailable);

    public IReadOnlyList<string> CompatibleExtensions => Extensions ?? Array.Empty<string>();
}
