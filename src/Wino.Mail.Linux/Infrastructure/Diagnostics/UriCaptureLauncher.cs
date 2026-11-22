#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// With <c>WINO_DEBUG_CAPTURE_URIS=&lt;file&gt;</c>, addresses are not opened in a browser: one redacted line per address is
/// appended to the file (scheme, host, path, parameter names, and the values of only <see cref="Shown"/>). Files and folders
/// still open. Agents check sign-in requests with it without a browser and without writing a secret anywhere.
/// </summary>
internal sealed class UriCaptureLauncher(IExternalLauncher inner, string path) : IExternalLauncher
{
    public const string Variable = "WINO_DEBUG_CAPTURE_URIS";

    private static readonly string[] Shown = ["redirect_uri", "response_type", "code_challenge_method", "scope", "prompt", "access_type"];
    private static readonly Lock Gate = new();

    public static void Register(IServiceCollection services)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path)) return;
        var previous = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IExternalLauncher));
        if (previous?.ImplementationFactory is not { } factory) return;
        services.Replace(ServiceDescriptor.Singleton<IExternalLauncher>(provider => new UriCaptureLauncher((IExternalLauncher)factory(provider), path)));
        Serilog.Log.Information("Debug: browser addresses are captured, not opened.");
    }

    public Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var line = Describe(uri);
        lock (Gate) File.AppendAllText(path, line + "\n");
        Serilog.Log.Information("Debug action: captured a browser address on {Host}{Path}.", uri.Host, uri.AbsolutePath);
        return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
    }

    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default) => inner.LaunchFileAsync(path, cancellationToken);

    public Task<PlatformOperationResult> LaunchFolderAsync(string path, CancellationToken cancellationToken = default) => inner.LaunchFolderAsync(path, cancellationToken);

    internal static string Describe(Uri uri)
    {
        var parts = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            parts.Add(Array.IndexOf(Shown, name) >= 0 ? $"{name}={value}" : name);
        }
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath} {string.Join(' ', parts)}";
    }
}
#endif
