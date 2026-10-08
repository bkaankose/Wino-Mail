using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class SemanticIndexJobRegistryTests
{
    [Fact]
    public async Task CancelAndWaitAsync_CancelsWorkerAndWaitsForCleanup()
    {
        var registry = new SemanticIndexJobRegistry();
        var accountId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = false;

        registry.TryStart(accountId, async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cleanedUp = true;
            }
        }, out _).Should().BeTrue();

        await started.Task;
        await registry.CancelAndWaitAsync(accountId);

        cleanedUp.Should().BeTrue();
        registry.IsRunning(accountId).Should().BeFalse();
    }

    [Fact]
    public async Task TryStart_MergesDuplicateAccountWork()
    {
        var registry = new SemanticIndexJobRegistry();
        var accountId = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.TryStart(accountId, _ => release.Task, out var first).Should().BeTrue();

        registry.TryStart(accountId, _ => Task.CompletedTask, out var duplicate).Should().BeFalse();
        duplicate.Should().BeSameAs(first);

        release.TrySetResult();
        await first;
    }

    [Fact]
    public async Task CancelAllAndWaitAsync_CancelsAndAwaitsEveryAccount()
    {
        var registry = new SemanticIndexJobRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;

        foreach (var accountId in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            registry.TryStart(accountId, async token =>
            {
                if (Interlocked.Increment(ref cleanupCount) == 1)
                    started.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    Interlocked.Increment(ref cleanupCount);
                }
            }, out _).Should().BeTrue();
        }

        await started.Task;
        await registry.CancelAllAndWaitAsync();

        cleanupCount.Should().Be(4);
    }
}
