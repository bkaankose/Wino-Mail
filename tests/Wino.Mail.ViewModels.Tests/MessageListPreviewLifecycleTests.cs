using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MessageListPreviewLifecycleTests
{
    [Fact]
    public async Task LeavingWhileRefreshIsPendingPreventsQueuedPreviewFromAddingRows()
    {
        var preferences = new Mock<IPreferencesService>();
        var dispatcher = new GatedDispatcher();
        var viewModel = Create(preferences.Object);
        viewModel.Dispatcher = dispatcher;

        var initialization = viewModel.InitializeNavigationAsync(NavigationMode.New, null!);
        preferences.Raise(service => service.PreferenceChanged += null!, preferences.Object, nameof(IPreferencesService.IsThreadingEnabled));
        viewModel.PreviewRefreshTask.IsCompleted.Should().BeFalse();
        dispatcher.PendingCount.Should().Be(1); // second refresh waits for the first.

        viewModel.OnNavigatedFrom(NavigationMode.Back, null!);
        await dispatcher.ExecuteNextAsync(); // display options already accepted.
        await dispatcher.ExecuteNextAsync(); // clears; inactive check skips Add.
        await initialization.WaitAsync(TimeSpan.FromSeconds(10));
        await viewModel.PreviewRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.PreviewMailCollection.Count.Should().Be(0);
        dispatcher.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task FailedPreviewRemainsObservableAndLaterPreferenceRefreshRecovers()
    {
        var preferences = new Mock<IPreferencesService>();
        var viewModel = Create(preferences.Object);
        viewModel.Dispatcher = new FailOnceDispatcher();

        await Assert.ThrowsAsync<IOException>(() => viewModel.InitializeNavigationAsync(NavigationMode.New, null!));
        viewModel.PreviewRefreshTask.IsFaulted.Should().BeTrue();

        preferences.Raise(service => service.PreferenceChanged += null!, preferences.Object, nameof(IPreferencesService.IsShowPreviewEnabled));
        await viewModel.PreviewRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.PreviewMailCollection.Count.Should().Be(1);
        viewModel.OnNavigatedFrom(NavigationMode.Back, null!);
    }

    private static MessageListPageViewModel Create(IPreferencesService preferences) =>
        new(preferences, Mock.Of<IThumbnailService>(), Mock.Of<IStatePersistanceService>(), Mock.Of<IDialogServiceBase>());

    private sealed class GatedDispatcher : IDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Completion)> _pending = new();
        private readonly SemaphoreSlim _available = new(0);
        public int PendingCount => _pending.Count;
        public Task ExecuteOnUIThread(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion)); _available.Release(); return completion.Task;
        }
        public async Task ExecuteNextAsync()
        {
            (await _available.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the expected dispatcher callback must be queued");
            _pending.TryDequeue(out var operation).Should().BeTrue();
            try { operation.Action(); operation.Completion.SetResult(); }
            catch (Exception exception) { operation.Completion.SetException(exception); }
        }
    }
    private sealed class FailOnceDispatcher : IDispatcher
    {
        private bool _failed;
        public Task ExecuteOnUIThread(Action action)
        {
            if (!_failed) { _failed = true; return Task.FromException(new IOException("Preview dispatcher unavailable.")); }
            action(); return Task.CompletedTask;
        }
    }
}
