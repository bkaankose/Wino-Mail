#nullable enable

namespace Wino.Services;

internal sealed record LocalizedRewriteRequest(
    string Html,
    string Mode,
    string Language,
    string Context);
