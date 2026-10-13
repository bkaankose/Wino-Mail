using Foundation;
using Wino.Core.Activation;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Turns scene activations (mailto, wino://, webcal(s), and .ics, .vcf and .eml files opened from Files or Share)
/// into <see cref="ActivationItem"/>s for the shared <see cref="ActivationRouter"/>, which holds the routing rules
/// (the Mac MacActivationCoordinator). URLs that arrive before the runtime started wait until <see cref="SetReady"/>.
/// Sign-in callbacks (msauth, the Google reverse client ID) never reach the router: the system web authentication
/// session receives them.
/// </summary>
public sealed class UIKitActivationCoordinator(ActivationRouter router)
{
    private readonly List<NSUrl> _pending = [];
    private readonly object _lock = new();
    private bool _ready;

    /// <summary>Handles URLs now, or after the runtime started.</summary>
    public Task HandleAsync(IReadOnlyList<NSUrl> urls)
    {
        var routed = urls.Where(url => !IsAuthenticationCallback(url)).ToArray();
        if (routed.Length == 0) return Task.CompletedTask;
        lock (_lock)
        {
            if (!_ready)
            {
                _pending.AddRange(routed);
                return Task.CompletedTask;
            }
        }
        return RouteAsync(routed);
    }

    /// <summary>The runtime started: route the URLs that arrived during launch.</summary>
    public Task SetReady()
    {
        NSUrl[] pending;
        lock (_lock)
        {
            _ready = true;
            pending = [.. _pending];
            _pending.Clear();
        }
        return pending.Length == 0 ? Task.CompletedTask : RouteAsync(pending);
    }

    private Task RouteAsync(IReadOnlyList<NSUrl> urls)
        => router.HandleAsync(urls.Select(url => new ActivationItem(Classify(url), url.AbsoluteString ?? string.Empty, url.IsFileUrl ? new SecurityScopedFile(url) : null)).ToArray());

    public static ActivationUriKind Classify(NSUrl url)
        => url.IsFileUrl ? ActivationUriClassifier.ClassifyFilePath(url.Path) : ActivationUriClassifier.Classify(url.AbsoluteString);

    private static bool IsAuthenticationCallback(NSUrl url)
        => url.Scheme is { } scheme && (scheme.StartsWith("msauth", StringComparison.OrdinalIgnoreCase)
            || scheme.StartsWith("com.googleusercontent.apps", StringComparison.OrdinalIgnoreCase));

    /// <summary>A file handed over by another app, read inside its security scope.</summary>
    private sealed class SecurityScopedFile(NSUrl url) : IActivationFile
    {
        public string? Path => url.Path;

        public IDisposable BeginAccess() => new Scope(url, url.StartAccessingSecurityScopedResource());

        private sealed class Scope(NSUrl url, bool started) : IDisposable
        {
            private bool _started = started;

            public void Dispose()
            {
                if (!_started) return;
                _started = false;
                url.StopAccessingSecurityScopedResource();
            }
        }
    }
}
