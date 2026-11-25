using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux;

/// <summary>
/// The app's folders and the Sentry address. Only a packaged Release build reports to Sentry (the address the other
/// platforms use, from <c>ApplicationConfiguration</c>); a development build runs on lab data and keeps it empty.
/// </summary>
public sealed class LinuxApplicationConfiguration : IApplicationConfiguration
{
    public required string ApplicationDataFolderPath { get; set; }
    public required string PublisherSharedFolderPath { get; set; }
    public required string ApplicationTempFolderPath { get; set; }
    public string SentryDNS { get; init; } = string.Empty;
}
