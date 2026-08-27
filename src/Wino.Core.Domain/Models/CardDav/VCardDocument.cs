using System.Collections.Generic;

namespace Wino.Core.Domain.Models.CardDav;

public sealed class VCardDocument
{
    public string Version { get; set; } = "3.0";
    public List<VCardProperty> Properties { get; } = [];
}

public sealed class VCardProperty
{
    public string Group { get; set; }
    public string Name { get; set; }
    public string OriginalName { get; set; }
    public List<VCardParameter> Parameters { get; } = [];
    public string Value { get; set; }
}

public sealed class VCardParameter
{
    public string Name { get; set; }
    public string OriginalName { get; set; }
    public List<string> Values { get; } = [];
}

public sealed record VCardHashes(string RawHash, string SemanticHash, string DomainHash);
