using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
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
    public async Task ImportClassificationPage_KeepsTheRawSignalsBehindEachDecision()
    {
        var signals = new ClassificationSignals(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["finance"] = 0.62, ["newsletter"] = 0.11 },
            0.87,
            0.5,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["normal"] = 0.5, ["high"] = 0.5 },
            "pay",
            0.45,
            0.18);

        await _store.ImportClassificationPageAsync(
            AccountId, [Classification("m1", "h1", include: true, signals)], [], Empty);

        var stored = (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"]))["m1"].Signals;

        // Retuning a threshold has to be possible against results that are already here,
        // so every probability survives the round trip, including the rejected ones.
        stored.LabelProbabilities["finance"].Should().Be(0.62);
        stored.BriefingProbability.Should().Be(0.87);
        stored.PriorityScore.Should().Be(0.5);
        stored.PriorityProbabilities["high"].Should().Be(0.5);
        stored.TopAction.Should().Be("pay");
        stored.TopActionProbability.Should().Be(0.45);
        stored.ActionConfidence.Should().Be(0.18);
    }

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
    public async Task ClassificationAndSummarizationImportIndependently()
    {
        await _store.ImportClassificationPageAsync(AccountId, [Classification("m1", "h1", include: true)], [], Empty);

        // Classification alone is a complete, usable result; Summarization simply has not arrived yet.
        (await _store.GetClassificationArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle();
        (await _store.GetSummaryArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();

        await _store.ImportSummaryPageAsync(AccountId, [Summary("m1", "h1")], [], Empty);

        (await _store.GetSummaryArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle()
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
        job.Summarization.IsAcknowledged.Should().BeFalse();
        job.IsFinished.Should().BeFalse();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Summarization);
        (await _store.GetJobAsync(jobId))!.IsFinished.Should().BeTrue();
    }

    [Fact]
    public async Task UnfinishedJobsSurviveAndAreListedUntilBothStagesAreAcknowledged()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));

        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Classification);
        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle("Summarization is still outstanding");

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Summarization);
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
        ClassificationSignals? signals = null)
        => new(
            new MailArtifactKey(remoteMessageId, hash),
            ["important"],
            "normal",
            "none",
            include,
            DateTime.UtcNow,
            signals ?? ClassificationSignals.Empty);

    private static SummaryArtifact Summary(string remoteMessageId, string hash)
        => new(new MailArtifactKey(remoteMessageId, hash), $"Headline {remoteMessageId}", "Summary.", DateTime.UtcNow);

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
