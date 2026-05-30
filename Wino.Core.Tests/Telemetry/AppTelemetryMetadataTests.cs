using FluentAssertions;
using Wino.Core.Domain.Models.Telemetry;
using Xunit;

namespace Wino.Core.Tests.Telemetry;

public sealed class AppTelemetryMetadataTests
{
    [Fact]
    public void Create_UsesDebugEnvironment_ForDebugBuilds()
    {
        var metadata = AppTelemetryMetadata.Create("2.0.21.0", "Wino.Mail.WinUI", isDebug: true);

        metadata.SentryEnvironment.Should().Be("debug");
        metadata.BuildConfiguration.Should().Be("Debug");
        metadata.SentryRelease.Should().Be("wino-mail@2.0.21.0");
        metadata.SentryDist.Should().Be("2.0.21.0");
    }

    [Fact]
    public void Create_UsesProductionEnvironment_ForReleaseBuilds()
    {
        var metadata = AppTelemetryMetadata.Create("2.0.21.0", "Wino.Mail.WinUI", isDebug: false);

        metadata.SentryEnvironment.Should().Be("production");
        metadata.BuildConfiguration.Should().Be("Release");
        metadata.SentryRelease.Should().Be("wino-mail@2.0.21.0");
    }

    [Fact]
    public void Create_NormalizesMissingVersion()
    {
        var metadata = AppTelemetryMetadata.Create(null, null, isDebug: false);

        metadata.AppVersion.Should().Be("unknown");
        metadata.PackageName.Should().Be("unknown");
        metadata.SentryRelease.Should().Be("wino-mail@unknown");
        metadata.SentryDist.Should().Be("unknown");
    }
}
