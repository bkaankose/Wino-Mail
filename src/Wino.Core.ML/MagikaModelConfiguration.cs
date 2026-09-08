#nullable enable
using System.Collections.Generic;

namespace Wino.Core.ML;

internal sealed record MagikaModelConfiguration(
    int BeginningSize,
    int EndSize,
    int BlockSize,
    int PaddingToken,
    int MinimumFileSize,
    double DefaultThreshold,
    string[] Labels,
    IReadOnlyDictionary<string, double> Thresholds,
    IReadOnlyDictionary<string, string> OverwriteMap);

internal sealed record MagikaContentTypeInformation(
    string? MimeType,
    string? Group,
    string? Description,
    string[] Extensions,
    bool IsText);
