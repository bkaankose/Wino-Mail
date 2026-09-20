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
    public async Task ImportJevPage_StoresArtifactsAndMarksBriefingCandidates()
    {
        await _store.ImportJevPageAsync(AccountId, [Jev("m1", "h1", include: true), Jev("m2", "h2", include: false)], [], Empty);

        var artifacts = await _store.GetJevArtifactsAsync(AccountId, ["m1", "m2"]);
        artifacts.Should().HaveCount(2);
        artifacts["m1"].IncludeInBriefing.Should().BeTrue();

        var candidates = await _store.GetBriefingCandidatesAsync(AccountId);
        candidates.Should().ContainSingle().Which.Key.RemoteMessageId.Should().Be("m1");
    }

    [Fact]
    public async Task ImportJevPage_IgnoresArtifactsWhoseHashNoLongerMatches()
    {
        // The message changed locally after the job was submitted.
        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { ["m1"] = "current-hash" };

        var result = await _store.ImportJevPageAsync(AccountId, [Jev("m1", "stale-hash", include: true)], [], desired);

        result.Imported.Should().Be(0);
        result.SkippedStale.Should().Be(1);
        (await _store.GetJevArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportJevPage_IsIdempotentAndKeepsTheOriginalArrivalTime()
    {
        await _store.ImportJevPageAsync(AccountId, [Jev("m1", "h1", include: true)], [], Empty);
        var firstImported = await _store.GetFirstImportedUtcAsync(AccountId, "m1");

        await Task.Delay(20);
        await _store.ImportJevPageAsync(AccountId, [Jev("m1", "h1", include: true)], [], Empty);

        // A duplicate result must not make an old card look new.
        (await _store.GetFirstImportedUtcAsync(AccountId, "m1")).Should().Be(firstImported);
        (await _store.GetJevArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle();
    }

    [Fact]
    public async Task JevAndLunaImportIndependently()
    {
        await _store.ImportJevPageAsync(AccountId, [Jev("m1", "h1", include: true)], [], Empty);

        // Jev alone is a complete, usable result; Luna simply has not arrived yet.
        (await _store.GetJevArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle();
        (await _store.GetLunaArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();

        await _store.ImportLunaPageAsync(AccountId, [Luna("m1", "h1")], [], Empty);

        (await _store.GetLunaArtifactsAsync(AccountId, ["m1"])).Should().ContainSingle()
            .Which.Value.Headline.Should().Be("Headline m1");
    }

    [Fact]
    public async Task StageAcknowledgementIsTrackedPerStage()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));

        await _store.MarkStageImportedAsync(jobId, MailIntelligenceStageKind.Jev);
        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Jev);

        var job = await _store.GetJobAsync(jobId);
        job!.Jev.IsAcknowledged.Should().BeTrue();
        job.Luna.IsAcknowledged.Should().BeFalse();
        job.IsFinished.Should().BeFalse();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Luna);
        (await _store.GetJobAsync(jobId))!.IsFinished.Should().BeTrue();
    }

    [Fact]
    public async Task UnfinishedJobsSurviveAndAreListedUntilBothStagesAreAcknowledged()
    {
        var jobId = Guid.NewGuid();
        await _store.UpsertJobAsync(Job(jobId));

        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle();

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Jev);
        (await _store.GetUnfinishedJobsAsync()).Should().ContainSingle("Luna is still outstanding");

        await _store.MarkStageAcknowledgedAsync(jobId, MailIntelligenceStageKind.Luna);
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
        await _store.ImportJevPageAsync(AccountId, [Jev("m1", "h1", include: true)], [], Empty);
        await _store.ImportJevPageAsync(otherAccount, [Jev("m2", "h2", include: true)], [], Empty);

        await _store.DeleteAccountAsync(AccountId);

        (await _store.GetJevArtifactsAsync(AccountId, ["m1"])).Should().BeEmpty();
        (await _store.GetJevArtifactsAsync(otherAccount, ["m2"])).Should().ContainSingle();
    }

    private static IReadOnlyDictionary<string, string> Empty { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static JevArtifact Jev(string remoteMessageId, string hash, bool include)
        => new(new MailArtifactKey(remoteMessageId, hash), ["important"], "normal", include, DateTime.UtcNow);

    private static LunaArtifact Luna(string remoteMessageId, string hash)
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
