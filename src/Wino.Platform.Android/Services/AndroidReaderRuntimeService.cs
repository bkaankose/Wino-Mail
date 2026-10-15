using Android.Content;
using AndroidX.WebKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>
/// The reader needs a System WebView. Some devices have it disabled or missing; then
/// <see cref="WebViewCompat.GetCurrentWebViewPackage(Context)"/> returns null or fails, and the reader is unavailable.
/// </summary>
public sealed class AndroidReaderRuntimeService(Context context, IDispatcher dispatcher) : IReaderRuntimeService
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            try { available = WebViewCompat.GetCurrentWebViewPackage(context) is not null; }
            catch (Java.Lang.Exception error)
            {
                Serilog.Log.Warning(error, "The System WebView package could not be read.");
                available = false;
            }
        }).ConfigureAwait(false);
        return available;
    }
}
