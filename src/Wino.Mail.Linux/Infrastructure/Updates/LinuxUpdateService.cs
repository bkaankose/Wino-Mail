using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Platform.Linux.Services;

namespace Wino.Mail.Linux.Infrastructure.Updates;

internal enum LinuxUpdateCheckStatus
{
    NotApplicable,
    UpToDate,
    Available,
    Failed,
}

internal sealed record LinuxUpdateCheckResult(LinuxUpdateCheckStatus Status, LinuxUpdateInfo? Update = null);

/// <summary>
/// The update check of a packaged copy (macOS: Sparkle). It reads <c>https://download.winomail.app/linux/&lt;channel&gt;/release.json</c>
/// once a day and on request, takes the entry of the format the copy was installed from (<see cref="Kind"/>), and
/// downloads a <c>.deb</c> or <c>.rpm</c> after checking its size and SHA-256. A development build (no
/// <see cref="LinuxDistribution"/> channel) or an unknown package format never checks. The requests carry no
/// identifier, cookie or locale.
/// </summary>
internal sealed class LinuxUpdateService : IApplicationLifecycleHook, IDisposable
{
    public const string AutomaticCheckKey = "Linux.Updates.AutomaticCheck";
    private const string LastCheckKey = "Linux.Updates.LastCheckUtc";
    private const string LastPromptedBuildKey = "Linux.Updates.LastPromptedBuild";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(15);
    private const uint FirstCheckDelaySeconds = 60;
    private const uint ScheduleTickSeconds = 60 * 60;

    private readonly IConfigurationService _configuration;
    private readonly IDispatcher _dispatcher;
    private readonly LinuxWindowPresence _presence;
    private readonly LinuxDistribution _distribution;
    private readonly string _version;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stopping = new();
    private uint _firstCheck;
    private uint _tick;
    private int _checking;
    private LinuxUpdateInfo? _pendingPrompt;

    public LinuxUpdateService(IConfigurationService configuration, IAppMetadataService metadata, IDispatcher dispatcher, LinuxWindowPresence presence)
    {
        _configuration = configuration;
        _dispatcher = dispatcher;
        _presence = presence;
        _distribution = LinuxDistribution.Current;
        _version = metadata.AppVersion;
        FeedUri = ResolveFeedUri(_distribution, out var debugOrigin);
        DebugOrigin = debugOrigin;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinoMail-Linux", _version));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    /// <summary>Raised on the main thread when an automatic check found a build the user was not asked about yet.</summary>
    public event Action<LinuxUpdateInfo>? PromptRequested;

    public Uri? FeedUri { get; }

    public bool IsAvailable => FeedUri is not null && Kind is not null;

    public bool IsBeta => _distribution.IsBeta;

    public string CurrentVersion => _version;

    // A Debug feed override on localhost also allows packages from its own origin.
    private Uri? DebugOrigin { get; }

    public bool AutomaticCheck
    {
        get => _configuration.Get(AutomaticCheckKey, true);
        set => _configuration.Set(AutomaticCheckKey, value);
    }

    public static LinuxPackageKind? Kind { get; } = LinuxPackageKind.For(LinuxDistribution.Current.Package, RuntimeInformation.OSArchitecture);

    public Task ServicesReadyAsync()
    {
        if (!IsAvailable) return Task.CompletedTask;
        Serilog.Log.Information("Updates: {Channel} channel, build {Build}, feed {Feed}.", _distribution.Channel, _distribution.Build, FeedUri);
        return _dispatcher.ExecuteOnUIThread(() =>
        {
            _firstCheck = GLib.Functions.TimeoutAddSeconds(GLib.Constants.PRIORITY_DEFAULT_IDLE, FirstCheckDelaySeconds, () =>
            {
                _firstCheck = 0;
                _ = RunScheduledAsync();
                return false;
            });
            // The app runs for days in the background, so the 24-hour rule is tested every hour.
            _tick = GLib.Functions.TimeoutAddSeconds(GLib.Constants.PRIORITY_DEFAULT_IDLE, ScheduleTickSeconds, () =>
            {
                _ = RunScheduledAsync();
                return true;
            });
        });
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        _stopping.Cancel();
        return _dispatcher.ExecuteOnUIThread(RemoveSources);
    }

    private void RemoveSources()
    {
        if (_firstCheck != 0) GLib.Functions.SourceRemove(_firstCheck);
        if (_tick != 0) GLib.Functions.SourceRemove(_tick);
        _firstCheck = _tick = 0;
    }

    private async Task RunScheduledAsync()
    {
        try { await RunScheduledCoreAsync(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Updates: the scheduled check failed."); }
    }

    private async Task RunScheduledCoreAsync()
    {
        if (_pendingPrompt is { } pending && _presence.HasWindow)
        {
            _pendingPrompt = null;
            Prompt(pending);
            return;
        }
        if (!AutomaticCheck || !IsDue()) return;
        var result = await CheckAsync(manual: false, _stopping.Token);
        if (result is { Status: LinuxUpdateCheckStatus.Available, Update: { } update } &&
            _configuration.Get(LastPromptedBuildKey, 0L) != update.Build)
        {
            if (_presence.HasWindow) Prompt(update);
            else _pendingPrompt = update;
        }
    }

    private void Prompt(LinuxUpdateInfo update)
    {
        _configuration.Set(LastPromptedBuildKey, update.Build);
        _ = _dispatcher.ExecuteOnUIThread(() => PromptRequested?.Invoke(update));
    }

    private bool IsDue()
    {
        var last = _configuration.Get(LastCheckKey, string.Empty);
        return !DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastCheck)
               || DateTime.UtcNow - lastCheck.ToUniversalTime() >= CheckInterval
               || lastCheck.ToUniversalTime() > DateTime.UtcNow;
    }

    /// <summary>Reads the feed. A failed automatic check is one log line; the caller shows a manual one.</summary>
    public async Task<LinuxUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (FeedUri is not { } feed || _distribution.Channel is not { } channel || Kind is not { } kind)
            return new LinuxUpdateCheckResult(LinuxUpdateCheckStatus.NotApplicable);
        if (Interlocked.Exchange(ref _checking, 1) == 1) return new LinuxUpdateCheckResult(LinuxUpdateCheckStatus.Failed);
        try
        {
            var bytes = await ReadFeedAsync(feed, cancellationToken);
            _configuration.Set(LastCheckKey, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (LinuxUpdateFeed.Parse(bytes, channel, kind, DebugOrigin) is not { } update)
            {
                Serilog.Log.Information("Updates: the feed describes no {Format} {Architecture} package for this channel.", kind.FeedKey, kind.Architecture);
                return new LinuxUpdateCheckResult(manual ? LinuxUpdateCheckStatus.Failed : LinuxUpdateCheckStatus.UpToDate);
            }
            var newer = LinuxUpdateFeed.IsNewer(update, _version, _distribution.Build);
            Serilog.Log.Information("Updates: {Kind} check found {Format} {Offered}; running {Version} ({Build}).",
                manual ? "manual" : "automatic", kind.FeedKey, update.DisplayVersion, _version, _distribution.Build);
            return new LinuxUpdateCheckResult(newer ? LinuxUpdateCheckStatus.Available : LinuxUpdateCheckStatus.UpToDate, newer ? update : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        {
            Serilog.Log.Information("Updates: the {Kind} check failed: {Reason}", manual ? "manual" : "automatic", exception.Message);
            return new LinuxUpdateCheckResult(LinuxUpdateCheckStatus.Failed);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private async Task<byte[]> ReadFeedAsync(Uri feed, CancellationToken cancellationToken)
    {
        if (feed.IsFile)
        {
            var info = new FileInfo(feed.LocalPath);
            if (info.Length > LinuxUpdateFeed.MaximumBytes) throw new InvalidDataException("The feed is too large.");
            return await File.ReadAllBytesAsync(info.FullName, cancellationToken);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        using var response = await _http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new HttpRequestException($"The feed returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > LinuxUpdateFeed.MaximumBytes) throw new InvalidDataException("The feed is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > LinuxUpdateFeed.MaximumBytes) throw new InvalidDataException("The feed is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Downloads the package through a <c>.part</c> file into the download folder and returns its path. The file is
    /// deleted when its size or SHA-256 differs from the feed, and on cancellation.
    /// </summary>
    public async Task<string> DownloadAsync(LinuxUpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var name = update.FileName;
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(update.Kind.Extension, StringComparison.Ordinal))
            throw new InvalidDataException("The package address has no usable file name.");
        var folder = DownloadFolder();
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        var partial = target + ".part";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            using var response = await _http.GetAsync(update.PackageUrl, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new HttpRequestException($"The package returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is { } announced && announced != update.Size)
                throw new InvalidDataException($"The server announces {announced} bytes; the feed says {update.Size}.");
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, linked.Token)) > 0)
                {
                    total += read;
                    if (total > update.Size) throw new InvalidDataException("The package is larger than the feed says.");
                    sha256.AppendData(chunk, 0, read);
                    await file.WriteAsync(chunk.AsMemory(0, read), linked.Token);
                    progress?.Report((double)total / update.Size);
                }
                if (total != update.Size) throw new InvalidDataException($"The package has {total} bytes; the feed says {update.Size}.");
            }
            var hash = Convert.ToHexStringLower(sha256.GetHashAndReset());
            if (hash != update.Sha256) throw new InvalidDataException("The package does not match the SHA-256 of the feed.");
            File.Move(partial, target, overwrite: true);
            Serilog.Log.Information("Updates: downloaded and verified {File} ({Size} bytes).", target, update.Size);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Serilog.Log.Warning("Updates: {Path} could not be deleted.", path); }
    }

    private static string DownloadFolder()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINO_DEBUG_UPDATE_DOWNLOADS") is { Length: > 0 } debugFolder) return debugFolder;
#endif
        if (GLib.Functions.GetUserSpecialDir(GLib.UserDirectory.DirectoryDownload) is { Length: > 0 } downloads) return downloads;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 ? Path.Combine(home, "Downloads") : Path.GetTempPath();
    }

    private static Uri? ResolveFeedUri(LinuxDistribution distribution, out Uri? debugOrigin)
    {
        debugOrigin = null;
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINO_DEBUG_UPDATE_FEED") is { Length: > 0 } debugFeed && distribution.IsPackaged)
        {
            var uri = Uri.TryCreate(debugFeed, UriKind.Absolute, out var absolute) ? absolute : new Uri(Path.GetFullPath(debugFeed));
            if (!uri.IsFile) debugOrigin = uri;
            return uri;
        }
#endif
        return distribution.Channel is { } channel ? new Uri($"https://download.winomail.app/linux/{channel}/release.json") : null;
    }

#if DEBUG
    internal Task DebugRunAutomaticAsync()
    {
        _configuration.Remove(LastCheckKey);
        _configuration.Remove(LastPromptedBuildKey);
        return RunScheduledAsync();
    }

    internal string DebugDescribe()
        => $"available={IsAvailable}\nfeed={FeedUri}\nchannel={_distribution.Channel}\npackage={_distribution.Package}\nkind={Kind}\nbuild={_distribution.Build}\nversion={_version}\n"
           + $"beta={IsBeta}\nautomatic={AutomaticCheck}\nlastCheck={_configuration.Get(LastCheckKey, string.Empty)}\n"
           + $"lastPrompted={_configuration.Get(LastPromptedBuildKey, 0L)}\npending={_pendingPrompt?.DisplayVersion}\n";
#endif

    public void Dispose()
    {
        _stopping.Dispose();
        _http.Dispose();
    }
}
