using Wino.Mail.Contracts.SemanticIndex;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class SemanticCoverageRulesTests
{
    [Fact]
    public void WaitingMessages_AreOutsideRangeAndOrderedNewestFirst()
    {
        var mailboxId = Guid.NewGuid();
        var state = State(mailboxId, "2026-02-01T00:00:00Z", "2026-03-01T00:00:00Z");
        var messages = new[]
        {
            new Message("inside", DateTimeOffset.Parse("2026-02-15T00:00:00Z")),
            new Message("older", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new Message("newer-b", DateTimeOffset.Parse("2026-04-01T00:00:00Z")),
            new Message("newer-a", DateTimeOffset.Parse("2026-04-01T00:00:00Z"))
        };

        var waiting = SemanticCoverageRules.GetWaitingNewestFirst(
            messages,
            message => message.ReceivedAtUtc,
            message => message.Id,
            state);

        Assert.Equal(["newer-a", "newer-b", "older"], waiting.Select(x => x.Id));
    }

    [Fact]
    public void ServerRangeContainingLocalRange_IsCovered()
    {
        var state = State(Guid.NewGuid(), "2025-01-01T00:00:00Z", "2026-04-01T00:00:00Z");
        DateTimeOffset[] localDates =
        [
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-03-01T00:00:00Z")
        ];

        Assert.True(SemanticCoverageRules.IsRangeCovered(localDates, state));
    }

    [Fact]
    public void MissingServerRange_MarksEveryMessageWaiting()
    {
        var messages = new[]
        {
            new Message("second", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new Message("first", DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
        };

        var waiting = SemanticCoverageRules.GetWaitingNewestFirst(
            messages,
            message => message.ReceivedAtUtc,
            message => message.Id,
            null);

        Assert.Equal(["first", "second"], waiting.Select(x => x.Id));
    }

    private static SemanticMailboxIndexStateDto State(Guid mailboxId, string oldest, string newest)
        => new(
            mailboxId,
            "openai-text-embedding-3-small-768-v1",
            "text-embedding-3-small",
            768,
            DateTimeOffset.Parse(oldest),
            DateTimeOffset.Parse(newest),
            10,
            0,
            10,
            DateTimeOffset.UtcNow);

    private sealed record Message(string Id, DateTimeOffset ReceivedAtUtc);
}
