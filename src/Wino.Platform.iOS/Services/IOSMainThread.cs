using Foundation;
using UIKit;

namespace Wino.Platform.iOS.Services;

/// <summary>Synchronous reads of UIKit state from any thread.</summary>
internal static class IOSMainThread
{
    /// <summary>Runs <paramref name="read"/> inline on the main thread, otherwise waits for the main thread to run it.</summary>
    public static T Read<T>(Func<T> read)
    {
        if (NSThread.IsMain) return read();
        Holder<T>? holder = null;
        UIApplication.SharedApplication.InvokeOnMainThread(() => holder = new Holder<T>(read()));
        return holder is not null ? holder.Value : throw new InvalidOperationException("The main thread did not complete the read.");
    }

    private sealed class Holder<T>(T value)
    {
        public T Value { get; } = value;
    }
}
