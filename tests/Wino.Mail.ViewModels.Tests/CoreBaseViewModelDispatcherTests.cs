using System;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CoreBaseViewModelDispatcherTests
{
    [Fact]
    public async Task ExecuteUIThreadAsync_WaitsForAsyncCallback()
    {
        var callbackRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel();
        var callbackCompleted = false;

        var dispatchTask = viewModel.ExecuteUIThreadAsync(async () =>
        {
            await callbackRelease.Task;
            callbackCompleted = true;
        });

        dispatchTask.IsCompleted.Should().BeFalse();

        callbackRelease.SetResult();
        await dispatchTask;

        callbackCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteUIThreadAsync_ReturnsCallbackResult()
    {
        var viewModel = CreateViewModel();

        var result = await viewModel.ExecuteUIThreadAsync(async () =>
        {
            await Task.Yield();
            return 42;
        });

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteUIThreadAsync_PropagatesCallbackException()
    {
        var viewModel = CreateViewModel();
        var expectedException = new InvalidOperationException("dispatcher callback failed");

        var action = async () =>
        {
            await Task.Yield();
            throw expectedException;
        };

        await viewModel.Invoking(viewModel => viewModel.ExecuteUIThreadAsync(action))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedException.Message);
    }

    private static CoreBaseViewModel CreateViewModel()
        => new()
        {
            Dispatcher = new ImmediateDispatcher()
        };

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
