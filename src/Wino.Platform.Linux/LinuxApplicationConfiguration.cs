using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux;

/// <summary>
/// The app's folders. The Sentry DSN is empty on purpose: the Linux head is a proof of concept that runs on lab
/// data, and an empty DSN keeps the SDK off until the head is released.
/// </summary>
public sealed class LinuxApplicationConfiguration : IApplicationConfiguration
{
    public required string ApplicationDataFolderPath { get; set; }
    public required string PublisherSharedFolderPath { get; set; }
    public required string ApplicationTempFolderPath { get; set; }
    public string SentryDNS => string.Empty;
}
