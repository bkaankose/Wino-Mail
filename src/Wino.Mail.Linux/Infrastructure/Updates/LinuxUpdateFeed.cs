using System.Globalization;
using System.Text.Json;

namespace Wino.Mail.Linux.Infrastructure.Updates;

internal sealed record LinuxUpdateInfo(LinuxPackageKind Kind, string Version, long Build, Uri PackageUrl, string Sha256, long Size, Uri? NotesUrl, Uri? DownloadPageUrl)
{
    public string DisplayVersion => $"{Version} ({Build.ToString(CultureInfo.InvariantCulture)})";

    public string FileName => Path.GetFileName(Uri.UnescapeDataString(PackageUrl.AbsolutePath));
}

/// <summary>
/// The update feed <c>linux/&lt;channel&gt;/release.json</c> written by <c>scripts/release/linux/build-linux-release.sh</c>.
/// Each package format has its own entry (<c>packages.deb.amd64</c>, <c>packages.arch.x86_64</c>,
/// <c>packages.rpm.x86_64</c>); a copy reads only the entry of the format it was installed from. Unknown fields are
/// ignored; a missing or malformed known field, another platform or channel, or an address outside the allowed origins
/// makes the feed describe no update.
/// </summary>
internal static class LinuxUpdateFeed
{
    public const int MaximumBytes = 256 * 1024;
    public const int Schema = 1;

    private static readonly string[] AllowedOrigins = ["https://download.winomail.app/", "https://winomail.app/", "https://www.winomail.app/"];

    public static LinuxUpdateInfo? Parse(ReadOnlySpan<byte> json, string channel, LinuxPackageKind kind, Uri? extraOrigin = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetInt64(root, "schema", out var schema) || schema < Schema) return null;
            if (GetString(root, "platform") != "linux" || GetString(root, "channel") != channel) return null;
            if (GetString(root, "version") is not { } version || !IsVersion(version)) return null;
            if (!TryGetInt64(root, "build", out var build) || build <= 0) return null;
            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object ||
                !packages.TryGetProperty(kind.FeedKey, out var format) || format.ValueKind != JsonValueKind.Object ||
                !format.TryGetProperty(kind.Architecture, out var package) || package.ValueKind != JsonValueKind.Object)
                return null;
            if (Allowed(GetString(package, "url"), extraOrigin) is not { } url || !url.AbsolutePath.EndsWith(kind.Extension, StringComparison.Ordinal)) return null;
            if (GetString(package, "sha256") is not { } sha256 || !IsSha256(sha256)) return null;
            if (!TryGetInt64(package, "size", out var size) || size <= 0) return null;
            return new LinuxUpdateInfo(kind, version, build, url, sha256.ToLowerInvariant(), size,
                Allowed(GetString(root, "notesUrl"), extraOrigin), Allowed(GetString(root, "downloadPageUrl"), extraOrigin));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the feed's version and build are newer than the running ones; versions compare first.</summary>
    public static bool IsNewer(LinuxUpdateInfo update, string currentVersion, long currentBuild)
    {
        if (!System.Version.TryParse(update.Version, out var offered)) return false;
        if (!System.Version.TryParse(currentVersion, out var current)) return true;
        var order = offered.CompareTo(current);
        return order > 0 || (order == 0 && update.Build > currentBuild);
    }

    private static bool IsVersion(string value)
        => value.Split('.') is { Length: 3 } parts && parts.All(static part => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit));

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static Uri? Allowed(string? value, Uri? extraOrigin)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            AllowedOrigins.Any(origin => uri.AbsoluteUri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)))
            return uri;
        return extraOrigin is not null && Uri.Compare(uri, extraOrigin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 ? uri : null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
}
