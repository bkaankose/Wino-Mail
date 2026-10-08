namespace Wino.Core.Domain.Models.Telemetry;

public sealed record AppTelemetryMetadata(
    string AppVersion,
    string PackageName,
    string BuildConfiguration,
    string SentryEnvironment,
    string SentryRelease,
    string SentryDist)
{
    public const string DebugEnvironment = "debug";
    public const string ProductionEnvironment = "production";
    public const string ReleaseNamePrefix = "wino-mail@";

    public static string GetEnvironment(bool isDebug)
        => isDebug ? DebugEnvironment : ProductionEnvironment;

    public static string GetBuildConfiguration(bool isDebug)
        => isDebug ? "Debug" : "Release";

    public static string GetRelease(string appVersion)
        => $"{ReleaseNamePrefix}{NormalizeAppVersion(appVersion)}";

    public static string NormalizeAppVersion(string appVersion)
        => string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim();

    public static AppTelemetryMetadata Create(string appVersion, string packageName, bool isDebug)
    {
        var normalizedVersion = NormalizeAppVersion(appVersion);

        return new AppTelemetryMetadata(
            normalizedVersion,
            string.IsNullOrWhiteSpace(packageName) ? "unknown" : packageName.Trim(),
            GetBuildConfiguration(isDebug),
            GetEnvironment(isDebug),
            GetRelease(normalizedVersion),
            normalizedVersion);
    }
}
