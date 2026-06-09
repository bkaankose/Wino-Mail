using FluentAssertions;
using Xunit;

namespace Wino.Ipc.Tests;

public class RpcOperationDeduplicatorTests
{
    [Fact]
    public void UnknownOperation_IsNotFound()
    {
        var deduplicator = new RpcOperationDeduplicator();

        deduplicator.TryGetCompletedResponse(Guid.NewGuid(), out _).Should().BeFalse();
    }

    [Fact]
    public void RecordedOperation_ReplaysPayload()
    {
        var deduplicator = new RpcOperationDeduplicator();
        var id = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3 };

        deduplicator.RecordCompletedOperation(id, payload);

        deduplicator.TryGetCompletedResponse(id, out var replayed).Should().BeTrue();
        replayed.Should().Equal(payload);
    }

    [Fact]
    public void CapacityOverflow_EvictsLeastRecentlyUsed()
    {
        var deduplicator = new RpcOperationDeduplicator(capacity: 3);

        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();

        deduplicator.RecordCompletedOperation(ids[0], [0]);
        deduplicator.RecordCompletedOperation(ids[1], [1]);
        deduplicator.RecordCompletedOperation(ids[2], [2]);

        // Touch the oldest entry so it becomes most recently used.
        deduplicator.TryGetCompletedResponse(ids[0], out _).Should().BeTrue();

        deduplicator.RecordCompletedOperation(ids[3], [3]);

        deduplicator.TryGetCompletedResponse(ids[1], out _).Should().BeFalse("the least recently used entry is evicted");
        deduplicator.TryGetCompletedResponse(ids[0], out _).Should().BeTrue();
        deduplicator.TryGetCompletedResponse(ids[2], out _).Should().BeTrue();
        deduplicator.TryGetCompletedResponse(ids[3], out _).Should().BeTrue();
    }

    [Fact]
    public void NullResponsePayload_IsReplayedAsNull()
    {
        var deduplicator = new RpcOperationDeduplicator();
        var id = Guid.NewGuid();

        deduplicator.RecordCompletedOperation(id, null);

        deduplicator.TryGetCompletedResponse(id, out var replayed).Should().BeTrue();
        replayed.Should().BeNull();
    }
}
