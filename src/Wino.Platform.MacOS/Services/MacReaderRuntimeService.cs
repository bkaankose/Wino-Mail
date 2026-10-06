using CoreGraphics;
using Foundation;
using WebKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>Confirms the native WebKit content process can load and execute a local document.</summary>
public sealed class MacReaderRuntimeService(IDispatcher dispatcher) : IReaderRuntimeService
{
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _verified;
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) return false;
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            if (_verified) return true;
            WKWebView? browser = null;
            WKWebViewConfiguration? configuration = null;
            ProbeNavigation? navigation = null;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    configuration = new WKWebViewConfiguration();
                    browser = new WKWebView(CGRect.Empty, configuration);
                    navigation = new ProbeNavigation(completion);
                    browser.NavigationDelegate = navigation;
                    browser.LoadHtmlString("<!doctype html><html><body>Wino runtime probe</body></html>", null);
                });
                bool available;
                try { available = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
                catch (TimeoutException) { available = false; }
                _verified = available;
                return available;
            }
            finally
            {
                // Even cancellation owns native stop/detach/disposal before returning to the caller.
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    navigation?.Release();
                    browser?.StopLoading();
                    if (browser is not null) browser.NavigationDelegate = null;
                    browser?.Dispose(); navigation?.Dispose(); configuration?.Dispose();
                });
            }
        }
        finally { _probeGate.Release(); }
    }
    private sealed class ProbeNavigation(TaskCompletionSource<bool> completion) : WKNavigationDelegate
    {
        private bool _released;
        public void Release() => _released = true;
        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (_released) return;
            webView.EvaluateJavaScript("document.readyState === 'complete' ? 'wino-ready' : 'not-ready'", (value, error) =>
            {
                if (!_released) completion.TrySetResult(error is null && value?.ToString() == "wino-ready");
            });
        }
        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) => completion.TrySetResult(false);
        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) => completion.TrySetResult(false);
        public override void ContentProcessDidTerminate(WKWebView webView) => completion.TrySetResult(false);
    }
}
