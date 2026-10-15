using System.Runtime.CompilerServices;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Lets a page view learn when its ViewModel's first load finished. The iOS pages reveal their content at the end of
/// their own InitializeAsync override; on Android the route's initializer runs the load (it outlives the fragment, which
/// a rotation recreates), so the initializer completes the ViewModel's task here and every view of the page awaits it.
/// </summary>
internal static class PageLoadTracker
{
    private static readonly ConditionalWeakTable<object, TaskCompletionSource> Loads = new();

    private static TaskCompletionSource Source(object viewModel)
        => Loads.GetValue(viewModel, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Runs <paramref name="load"/> and marks the ViewModel loaded whether it succeeded or not.</summary>
    public static async Task TrackAsync(object viewModel, Func<Task> load)
    {
        try { await load(); }
        finally { Source(viewModel).TrySetResult(); }
    }

    /// <summary>Completes when the ViewModel's first load finished.</summary>
    public static Task WhenLoaded(object viewModel) => Source(viewModel).Task;
}
