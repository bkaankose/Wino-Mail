namespace Wino.Core.Domain.Interfaces;

public interface IAppMetadataService
{
    string AppVersion { get; }
    string PackageName { get; }
    string BuildConfiguration { get; }
    string SentryEnvironment { get; }
    string SentryRelease { get; }
    string SentryDist { get; }
}
