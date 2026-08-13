using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Sentry;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Telemetry;

public sealed class WinoTelemetryPolicyTests
{
    [Fact]
    public void Sanitizer_DropsSensitiveIdentifiersAndValues()
    {
        var properties = WinoTelemetrySanitizer.CreateSafeProperties(new Dictionary<string, string>
        {
            ["provider"] = "IMAP4",
            ["diagnostic_id"] = "11111111-1111-1111-1111-111111111111",
            ["account_id"] = "22222222-2222-2222-2222-222222222222",
            ["folder_name"] = "Inbox",
            ["email"] = "person@example.com",
            ["authorization"] = "Bearer secret-token",
            ["service"] = "https://mail.example.com/private?token=secret"
        });

        properties.Should().Contain("provider", "IMAP4");
        properties.Should().Contain("diagnostic_id", "11111111-1111-1111-1111-111111111111");
        properties.Keys.Should().NotContain(["account_id", "folder_name", "email", "authorization", "service"]);
    }

    [Fact]
    public void Sanitizer_TruncatesLongValues()
    {
        var properties = WinoTelemetrySanitizer.CreateSafeProperties(new Dictionary<string, string>
        {
            ["exception_type"] = new string('x', WinoTelemetrySanitizer.MaxPropertyValueLength + 20)
        });

        properties["exception_type"].Should().HaveLength(WinoTelemetrySanitizer.MaxPropertyValueLength);
    }

    [Fact]
    public void Deduplicator_SuppressesRepeatedKeyWithinWindow()
    {
        var deduplicator = new WinoTelemetryDeduplicator();
        var now = new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

        deduplicator.ShouldSend("sync-key", TimeSpan.FromMinutes(30), now, out var firstSuppressed)
            .Should().BeTrue();
        deduplicator.ShouldSend("sync-key", TimeSpan.FromMinutes(30), now.AddMinutes(5), out _)
            .Should().BeFalse();
        deduplicator.ShouldSend("sync-key", TimeSpan.FromMinutes(30), now.AddMinutes(31), out var laterSuppressed)
            .Should().BeTrue();

        firstSuppressed.Should().Be(0);
        laterSuppressed.Should().Be(1);
    }

    [Fact]
    public void Deduplicator_EmitsChangedSignatureImmediately()
    {
        var deduplicator = new WinoTelemetryDeduplicator();
        var now = DateTimeOffset.UtcNow;

        deduplicator.ShouldSend("network", TimeSpan.FromMinutes(30), now, out _).Should().BeTrue();
        deduplicator.ShouldSend("authentication", TimeSpan.FromMinutes(30), now, out _).Should().BeTrue();
    }

    [Fact]
    public void LogRedactor_RemovesCommonSensitiveData()
    {
        const string input =
            "Email=person@example.com Authorization=Bearer abc.def Token=secret " +
            @"Path=C:\Users\Person\AppData\Local\Wino\client.log " +
            "Url=https://mail.example.com/private/path?token=secret";

        var redacted = DiagnosticLogRedactor.Redact(input);

        redacted.Should().NotContain("person@example.com");
        redacted.Should().NotContain("abc.def");
        redacted.Should().NotContain(@"C:\Users\Person");
        redacted.Should().NotContain("/private/path");
        redacted.Should().Contain("https://mail.example.com");
    }

    [Theory]
    [InlineData(SynchronizationCompletedState.Success, false)]
    [InlineData(SynchronizationCompletedState.Canceled, false)]
    [InlineData(SynchronizationCompletedState.Failed, true)]
    [InlineData(SynchronizationCompletedState.PartiallyCompleted, true)]
    public void SynchronizationPolicy_OnlyUploadsFailures(
        SynchronizationCompletedState state,
        bool expected)
    {
        Wino.Core.Services.SynchronizationManager.ShouldTrackSynchronizationTelemetry(state)
            .Should().Be(expected);
    }

    [Fact]
    public void TelemetryService_DoesNotSendWhenLoggingIsDisabled()
    {
        var sink = new RecordingTelemetrySink();
        var service = CreateTelemetryService(sink, isEnabled: false);

        service.TrackEvent(new WinoTelemetryEvent
        {
            Name = "sync_failure",
            Tags = new Dictionary<string, string> { ["provider"] = "IMAP4" }
        });

        sink.Breadcrumbs.Should().BeEmpty();
        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public void TelemetryService_EnrichesAndSanitizesCapturedEvent()
    {
        var sink = new RecordingTelemetrySink();
        var service = CreateTelemetryService(sink, isEnabled: true);

        service.TrackEvent(new WinoTelemetryEvent
        {
            Name = "sync_failure",
            Level = WinoTelemetryLevel.Warning,
            Tags = new Dictionary<string, string>
            {
                ["provider"] = "IMAP4",
                ["email"] = "person@example.com"
            },
            Context = new Dictionary<string, string>
            {
                ["issue_count"] = "2",
                ["folder_name"] = "Inbox"
            },
            Fingerprint = ["sync_failure", "IMAP4"]
        });

        sink.Breadcrumbs.Should().ContainSingle();
        sink.Events.Should().ContainSingle();

        var sentryEvent = sink.Events.Single();
        sentryEvent.User!.Id.Should().Be("11111111-1111-1111-1111-111111111111");
        sentryEvent.Tags.Should().Contain("app_mode", "calendar");
        sentryEvent.Tags.Should().Contain("provider", "IMAP4");
        sentryEvent.Tags.Keys.Should().NotContain("email");
        sentryEvent.Fingerprint.Should().Equal("sync_failure", "IMAP4");
        sentryEvent.Contexts.TryGetValue("diagnostics", out var diagnostics).Should().BeTrue();
        diagnostics.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["issue_count"] = "2"
        });
    }

    [Fact]
    public void TelemetryService_BreadcrumbOnlyEventDoesNotCreateIssue()
    {
        var sink = new RecordingTelemetrySink();
        var service = CreateTelemetryService(sink, isEnabled: true);

        service.TrackEvent(new WinoTelemetryEvent
        {
            Name = "imap_connection_test_completed",
            CaptureAsEvent = false,
            Tags = new Dictionary<string, string> { ["result"] = "success" }
        });

        sink.Breadcrumbs.Should().ContainSingle();
        sink.Events.Should().BeEmpty();
    }

    private static WinoTelemetryService CreateTelemetryService(
        RecordingTelemetrySink sink,
        bool isEnabled)
    {
        var contextProvider = new Mock<IWinoTelemetryContextProvider>();
        contextProvider
            .Setup(x => x.GetCurrent())
            .Returns(new WinoTelemetryContextSnapshot(
                "11111111-1111-1111-1111-111111111111",
                "calendar",
                "2.0.21.0",
                "Wino.Mail.WinUI",
                "Debug",
                "debug",
                "wino-mail@2.0.21.0",
                "2.0.21.0",
                isEnabled));

        return new WinoTelemetryService(
            contextProvider.Object,
            Mock.Of<ILogger<WinoTelemetryService>>(),
            sink);
    }

    private sealed class RecordingTelemetrySink : IWinoTelemetrySink
    {
        public List<(string Message, IReadOnlyDictionary<string, string> Data)> Breadcrumbs { get; } = [];

        public List<SentryEvent> Events { get; } = [];

        public void AddBreadcrumb(string message, IReadOnlyDictionary<string, string> data)
            => Breadcrumbs.Add((message, data));

        public void CaptureEvent(SentryEvent sentryEvent)
            => Events.Add(sentryEvent);
    }
}
