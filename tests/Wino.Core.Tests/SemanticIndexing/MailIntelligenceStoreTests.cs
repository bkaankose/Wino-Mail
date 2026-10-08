using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

/// <summary>
/// Import and job behaviour of the device-local intelligence database.
/// </summary>
public sealed class MailIntelligenceStoreTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wino-intel-{Guid.NewGuid():N}");
    private MailIntelligenceStore _store = null!;

    private static readonly Guid AccountId = Guid.Parse("2b0a9f2e-1c44-4a1a-9f0a-7a1e3d5b6c70");
    private static readonly Guid MailboxId = Guid.Parse("8d0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(_folder);
        _store = new MailIntelligenceStore(configuration.Object);
        return _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    [Fact]
    public async Task ImportClassificationPage_StoresArtifactsAndMarksBriefingCandidates()
    {
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true), Classification("m2", "h2", include: false)], [], Empty);

        var artifacts = await _store.GetClassificationArtifactsAsync(AccountId, ["m1", "m2"]);
        artifacts.Should().HaveCount(2);
        artifacts["m1"].IncludeInBriefing.Should().BeTrue();

        var candidates = await _store.GetBriefingCandidatesAsync(AccountId);
        candidates.Should().ContainSingle().Which.Key.RemoteMessageId.Should().Be("m1");
    }

    [Fact]
    public async Task ImportClassificationPage_KeepsTheHints()
    {
        await _store.ImportClassificationPageAsync(
            AccountId, [Classification("m1", "h1", include: false, hints: ["oneTimeCode", "calendarEvent"])], [], Empty);

        var stored = (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"]))["m1"];

        stored.Hints.Should().Equal("oneTimeCode", "calendarEvent");
    }

    [Fact]
    public async Task ImportEnrichmentPage_KeepsTypedSmartActions()
    {
        var start = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Unspecified);
        MailSmartAction[] actions =
        [
            new OneTimeCodeAction("Your code is 481 223", "481223", "sign-in", 10),
            new CalendarEventAction("Standup at 9:30", "Standup", start, null, false, "Europe/Istanbul", "Room 4", null),
            new ShipmentAction("Tracking 1Z999", "UPS", "1Z999", "https://example.test/track", new DateOnly(2026, 10, 3)),
        ];

        await _store.ImportEnrichmentPageAsync(
            AccountId, [new EnrichmentArtifact(new MailArtifactKey("m1", "h1"), string.Empty, string.Empty, actions, DateTime.UtcNow)], [], Empty);

        var stored = (await _store.GetEnrichmentArtifactsAsync(AccountId, ["m1"]))["m1"];

        stored.Actions.Should().HaveCount(3);
        stored.Actions[0].Should().BeOfType<OneTimeCodeAction>().Which.Code.Should().Be("481223");
        stored.Actions[1].Should().BeOfType<CalendarEventAction>().Which.Start.Should().Be(start);
        stored.Actions[2].Should().BeOfType<ShipmentAction>().Which.ExpectedDelivery.Should().Be(new DateOnly(2026, 10, 3));
    }

    [Fact]
    public void StoredActionsThatCannotBeRead_AreDroppedRatherThanFailing()
        => MailIntelligenceStore.DeserializeActions("""[{"kind":"somethingNew","evidence":"x"}]""").Should().BeEmpty();

    [Fact]
    public async Task ImportClassificationPage_IgnoresArtifactsWhoseHashNoLongerMatches()
    {
        // The message changed locally after the job was submitted.
        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { ["m1"] = "current-hash" };

        var result = await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "stale-hash", include: true)], [], desired);

        result.Imported.Should().Be(0);
        result.SkippedStale.Should().Be(1);
        (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportClassificationPage_IsIdempotentAndKeepsTheOriginalArrivalTime()
    {
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true)], [], Empty);
        var firstImported = await _store.GetFirstImportedUtcAsync(AccountId, "m1");

        await Task.Delay(20);
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true)], [], Empty);

        // A duplicate result must not make an old card look new.
        (await _store.GetFirstImportedUtcAsync(AccountId, "m1")).Should().Be(firstImported);
        (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle();
    }

    [Fact]
    public async Task ClassificationAndEnrichmentImportIndependently()
    {
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true)], [], Empty);

        // Classification alone is a complete, usable result; Enrichment simply has not arrived yet.
        (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle();
        (await _store.GetEnrichmentArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();

        await _store.ImportEnrichmentPageAsync(AccountId, [Summary("m1", "h1")], [], Empty);

        (await _store.GetEnrichmentArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle()
            .Which.Value.Headline.Should().Be("Headline m1");
    }

    [Fact]
    public async Task StageAcknowledgementIsTrackedPerStage()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));

        await _store.MarkStageImportedAsync(jobId, MailIntelligenceStageKind.Classification);
        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Classification);

        var job = await _store.GetJobAsync(jobId);
        job!.Classification.IsAcknowledged.Should().BeTrue();
        job.Enrichment.IsAcknowledged.Should().BeFalse();
        job.IsFinished.Should().BeFalse();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Enrichment);
        (await _store.GetJobAsync(jobId))!.IsFinished.Should().BeTrue();
    }

    [Fact]
    public async Task UnfinishedJobsSurviveAndAreListedUntilBothStagesAreAcknowledged()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));

        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Classification);
        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle("Enrichment is still outstanding");

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Enrichment);
        (await _store.GetUnfinishedJobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task JobsPersistAcrossAReopen()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));
        await _store.DisposeAsync();

        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(_folder);
        _store = new MailIntelligenceStore(configuration.Object);
        await _store.InitializeAsync();

        // Polling has to resume after a restart, so the job registry is durable.
        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle().Which.JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task IgnoringIsKeyedOnContentHash()
    {
        await _store.SetIgnoredAsync(AccountId, "m1", "h1", isIgnored: true);

        var ignored = await _store.GetIgnoredAsync(AccountId);
        ignored["m1"].Should().Be("h1");

        await _store.SetIgnoredAsync(AccountId, "m1", "h1", isIgnored: false);
        (await _store.GetIgnoredAsync(AccountId)).Should().BeEmpty();
    }

    [Fact]
    public async Task BriefingOpenAndViewStatePersistForTheAccount()
    {
        await _store.MarkBriefingOpenedAsync(AccountId);

        var (openedUtc, viewedUtc) = await _store.GetBriefingViewStateAsync(AccountId);
        openedUtc.Should().NotBeNull();
        viewedUtc.Should().BeNull();

        await _store.MarkBriefingViewedAsync(AccountId);

        var (persistedOpenedUtc, persistedViewedUtc) = await _store.GetBriefingViewStateAsync(AccountId);
        persistedOpenedUtc.Should().Be(openedUtc);
        persistedViewedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAccountRemovesEverythingForThatAccountOnly()
    {
        var otherAccount = Guid.NewGuid();
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true)], [], Empty);
        await _store.ImportClassificationPageAsync(otherAccount, [Classification("m2", "h2", include: true)], [], Empty);

        await _store.DeleteAccountAsync(AccountId);

        (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();
        (await _store.GetClassificationArtifactsAsync(otherAccount, ["m2"])).Should().ContainSingle();
    }

    private static IReadOnlyDictionary<string, string> Empty { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static ClassificationArtifact Classification(
        string remoteMessageId,
        string hash,
        bool include,
        IReadOnlyList<string>? hints = null)
        => new(
            new MailArtifactKey(remoteMessageId, hash),
            ["important"],
            "normal",
            hints ?? [],
            include,
            DateTime.UtcNow);

    private static EnrichmentArtifact Summary(string remoteMessageId, string hash)
        => new(new MailArtifactKey(remoteMessageId, hash), $"Headline {remoteMessageId}", "Summary.", [], DateTime.UtcNow);

    private static MailIntelligenceJobState Job(Guid jobId) => new(
        jobId,
        AccountId,
        MailboxId,
        2,
        MailIntelligenceJobStatuses.Pending,
        new MailIntelligenceStageState(MailIntelligenceStageStatuses.Pending, 0, null, false, false),
        new MailIntelligenceStageState(MailIntelligenceStageStatuses.Pending, 0, null, false, false),
        0,
        null,
        DateTime.UtcNow,
        DateTime.UtcNow);
}
