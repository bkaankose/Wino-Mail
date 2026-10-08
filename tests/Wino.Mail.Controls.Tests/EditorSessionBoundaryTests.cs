using System.Text.Json;
using Wino.Editor;
using Xunit;

namespace Wino.Core.Tests;

public sealed class EditorSessionBoundaryTests
{
    [Fact]
    public async Task TerminalDisposalCancelsReadinessWaitAndRunsOnlyOnceAcrossConcurrentCallers()
    {
        var queue = new SessionOperationQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = queue.RunAsync(async token => { entered.SetResult(); await neverReady.Task.WaitAsync(token); });
        await entered.Task;
        int disposalCount = 0;
        var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await queue.DisposeAsync(() => { Interlocked.Increment(ref disposalCount); return ValueTask.CompletedTask; }))).ToArray();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, disposalCount);
    }

    [Fact]
    public async Task CanceledMutationFinishesBeforeTheNextMutationStarts()
    {
        var queue = new SessionOperationQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var first = queue.RunAsync(async () => { entered.SetResult(); await release.Task; }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        bool secondStarted = false;
        var second = queue.RunAsync(() => { secondStarted = true; return Task.CompletedTask; });
        Assert.False(secondStarted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.True(secondStarted);
    }

    [Fact]
    public async Task QueuedCancellationNeverMutatesAndDisposalWaitsForActiveMutation()
    {
        var queue = new SessionOperationQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync(() => release.Task);
        using var cancellation = new CancellationTokenSource();
        bool queuedMutated = false;
        var queued = queue.RunAsync(() => { queuedMutated = true; return Task.CompletedTask; }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        bool disposed = false;
        var disposal = queue.DisposeAsync(() => { disposed = true; return ValueTask.CompletedTask; }).AsTask();
        Assert.False(disposed);
        release.SetResult();
        await first;
        await disposal;
        Assert.True(disposed);
        Assert.False(queuedMutated);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.RunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public void BridgeRoundTripPreservesGenerationShortcutsAndNonAsciiSelection()
    {
        var message = new EditorMessage
        {
            Type = "selectionState", SessionId = "current-document",
            State = new EditorSelectionState { Bold = true, FontFamily = "日本語", ImageAltText = "Zażółć" },
            Gesture = new EditorApplicationShortcutGesture("Enter", true, false, true)
        };
        string json = JsonSerializer.Serialize(message, EditorJsonContext.Default.EditorMessage);
        var restored = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorMessage);
        Assert.Equal(message, restored);
        var reader = JsonSerializer.Deserialize("{\"type\":\"ready\",\"sessionId\":\"reader-generation\"}", EditorJsonContext.Default.RendererMessage);
        Assert.Equal("reader-generation", reader!.SessionId);
    }

    [Fact]
    public async Task CanonicalEmbeddedDocumentsLoadWithAuthorizedScriptsAndSanitizers()
    {
        string reader = await EditorDocumentAssets.GetReaderDocumentAsync(false);
        string editor = await EditorDocumentAssets.GetEditorDocumentAsync();
        Assert.Contains("Content-Security-Policy", reader);
        Assert.Contains("DOMPurify", reader);
        Assert.Contains("Readability", reader);
        Assert.Contains("<script nonce=", editor);
        Assert.DoesNotContain("<script defer src=", editor);
    }
}
