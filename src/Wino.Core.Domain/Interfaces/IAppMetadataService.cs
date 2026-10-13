namespace Wino.Core.Domain.Interfaces;

public interface IAppMetadataService
{
    string AppVersion { get; }
    string PackageName { get; }
    string BuildConfiguration { get; }
    string SentryEnvironment { get; }
    string SentryRelease { get; }
    string SentryDist { get; }

    /// <summary>User-facing platform name, such as "Windows" or "Mac", used in text like the default signature.</summary>
    string PlatformDisplayName { get; }
}
