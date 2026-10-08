using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.WhatsNew;

/// <summary>
/// The release notes of one version, read from Assets\WhatsNew\&lt;version&gt;.json.
/// </summary>
public class WhatsNewRelease
{
    /// <summary>Three-part version, e.g. "2.1.3". The package revision is not part of it.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Highlighted by the maintainer. Shown with a star in the version selector.</summary>
    [JsonPropertyName("isStarred")]
    public bool IsStarred { get; set; }

    [JsonPropertyName("features")]
    public List<WhatsNewFeature> Features { get; set; } = [];

    /// <summary>The version normalized to Major.Minor.Build, or null when it does not parse.</summary>
    [JsonIgnore]
    public Version? ParsedVersion => TryNormalizeVersion(Version, out var version) ? version : null;

    /// <summary>
    /// Parses "2.1.3" or a package version such as "2.1.3.0" into a three-part version, so a file
    /// version and the running package version compare equal regardless of the revision.
    /// </summary>
    public static bool TryNormalizeVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(text) || !System.Version.TryParse(text.Trim(), out var parsed))
            return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }
}
