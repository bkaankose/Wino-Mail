using FluentAssertions;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CoalescedRefreshTests
{
    [Fact]
    public async Task BurstOfRequests_RunsTheRefreshOnce()
    {
        var count = 0;
        var completed = new TaskCompletionSource();
        var refresh = new CoalescedRefresh(() =>
        {
            Interlocked.Increment(ref count);
            completed.TrySetResult();
            return Task.CompletedTask;
        });

        for (var i = 0; i < 8; i++)
            refresh.Request();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        count.Should().Be(1);
    }

    [Fact]
    public async Task RequestAfterRefresh_RunsAgain()
    {
        var count = 0;
        var signal = new SemaphoreSlim(0);
        var refresh = new CoalescedRefresh(() =>
        {
            Interlocked.Increment(ref count);
            signal.Release();
            return Task.CompletedTask;
        });

        refresh.Request();
        (await signal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        refresh.Request();
        (await signal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        count.Should().Be(2);
    }
}
