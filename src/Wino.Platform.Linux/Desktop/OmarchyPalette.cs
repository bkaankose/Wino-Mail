namespace Wino.Platform.Linux.Desktop;

/// <summary>
/// The colours of Omarchy's active theme: <c>theme/colors.toml</c> (flat <c>key = "#rrggbb"</c> lines) and the name in
/// <c>theme.name</c>, both below Omarchy's <c>current</c> folder.
/// </summary>
public sealed record OmarchyPalette(string Name, bool IsDark, IReadOnlyDictionary<string, string> Colors)
{
    public const string ThemeFolderName = "theme";
    public const string ThemeNameFileName = "theme.name";
    public const string ColorsFileName = "colors.toml";

    public string? Color(string key) => Colors.TryGetValue(key, out var value) ? value : null;

    /// <summary>The palette of the theme below <paramref name="currentFolder"/>, or null when it is unreadable or lacks a background and a foreground.</summary>
    public static OmarchyPalette? TryLoad(string currentFolder)
    {
        try
        {
            var theme = Path.Combine(currentFolder, ThemeFolderName);
            var colorsPath = Path.Combine(theme, ColorsFileName);
            if (!File.Exists(colorsPath)) return null;
            var colors = ParseColors(File.ReadAllText(colorsPath));
            if (!colors.ContainsKey("background") || !colors.ContainsKey("foreground")) return null;

            var namePath = Path.Combine(currentFolder, ThemeNameFileName);
            var name = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : string.Empty;
            if (name.Length == 0) name = Path.GetFileName(Path.TrimEndingDirectorySeparator(currentFolder));
            var light = (colors.TryGetValue("mode", out var mode) && string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase))
                || File.Exists(Path.Combine(theme, "light.mode"));
            colors.Remove("mode");
            return new OmarchyPalette(name, !light, colors);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>#rgb</c> and <c>#rrggbb</c> values of a flat TOML file, plus <c>mode</c>; sections and other values are skipped.</summary>
    public static Dictionary<string, string> ParseColors(string text)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment].TrimEnd();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1].Trim();
            if (string.Equals(key, "mode", StringComparison.OrdinalIgnoreCase) || IsHexColor(value)) colors[key] = value.ToLowerInvariant();
        }
        return colors;
    }

    private static bool IsHexColor(string value)
        => value.Length is 4 or 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public bool Equals(OmarchyPalette? other)
        => other is not null && Name == other.Name && IsDark == other.IsDark
            && Colors.Count == other.Colors.Count
            && Colors.All(pair => other.Colors.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public override int GetHashCode() => HashCode.Combine(Name, IsDark, Colors.Count);
}
