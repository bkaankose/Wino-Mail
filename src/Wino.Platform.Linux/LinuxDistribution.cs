using System.Globalization;

namespace Wino.Platform.Linux;

/// <summary>
/// How this copy was distributed, read once from the text file <c>distribution</c> that a package writes beside the
/// executable (<c>channel=beta</c>, <c>package=deb</c>, <c>build=263261530</c>). <c>package</c> is <c>deb</c>,
/// <c>arch</c> or <c>rpm</c>, the format whose entry the update check reads. Without the file the copy is a
/// development build: no channel, build 0, no telemetry and no update check.
/// </summary>
public sealed class LinuxDistribution
{
    public const string FileName = "distribution";

    private static readonly Lazy<LinuxDistribution> CurrentValue = new(Load);

    private LinuxDistribution(string? channel, string? package, long build)
    {
        Channel = channel;
        Package = package;
        Build = build;
    }

    public static LinuxDistribution Current => CurrentValue.Value;

    public string? Channel { get; }
    public string? Package { get; }
    public long Build { get; }

    public bool IsPackaged => Channel is not null;
    public bool IsBeta => Channel == "beta";

    public static LinuxDistribution Parse(IEnumerable<string> lines)
    {
        string? channel = null, package = null;
        long build = 0;
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0) continue;
            switch (key)
            {
                case "channel" when IsToken(value): channel = value; break;
                case "package" when IsToken(value): package = value; break;
                case "build" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number): build = number; break;
            }
        }
        return new LinuxDistribution(channel, package, build);
    }

    // The channel becomes a path segment of the update feed address.
    private static bool IsToken(string value)
    {
        foreach (var character in value)
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') return false;
        return true;
    }

    private static LinuxDistribution Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINO_DEBUG_DISTRIBUTION") is { Length: > 0 } debugPath) path = debugPath;
#endif
        try
        {
            return File.Exists(path) ? Parse(File.ReadLines(path)) : new LinuxDistribution(null, null, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LinuxDistribution(null, null, 0);
        }
    }
}
