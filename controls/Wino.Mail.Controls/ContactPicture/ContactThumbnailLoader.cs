using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Wino.Mail.Controls.ContactPicture;

/// <summary>
/// Self-contained avatar resolution pipeline used by <see cref="WinoContactPicture"/>:
/// Gravatar -> Favicon -> null (caller falls back to initials).
/// Downloads are cached to disk permanently; a definitive "not found" answer is
/// also persisted (as an empty .miss marker) and never retried. Only transient
/// failures (network down, timeouts) are retried, after an in-memory backoff.
/// </summary>
public static class ContactThumbnailLoader
{
    public const int AvatarPixelSize = 48;

    private const string ImageExtension = ".img";
    private const string MissExtension = ".miss";

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Positive entries point at an existing cache file. Negative entries with a
    // null RetryAfterUtc are permanent (backed by a .miss marker); with a value
    // they are transient and expire in-memory.
    private static readonly ConcurrentDictionary<string, CacheEntry> _memoryCache = new();

    // Single-flight: N controls asking for the same cache key await one fetch.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflightRequests = new();

    private static readonly HashSet<string> _excludedFaviconDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com",
        "outlook.com",
        "hotmail.com",
        "live.com",
        "yahoo.com",
        "icloud.com",
        "aol.com",
        "protonmail.com",
        "zoho.com",
        "mail.com",
        "gmx.com",
        "yandex.com",
        "yandex.ru",
        "tutanota.com",
        "mail.ru",
        "rediffmail.com",
    };

    private static string _configuredCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wino.Mail.Controls",
        "AvatarCache");

    private static readonly Lazy<string> _cacheRoot = new(() =>
    {
        Directory.CreateDirectory(_configuredCacheRoot);
        return _configuredCacheRoot;
    });

    /// <summary>
    /// Root folder for cached avatar files. Set at application startup, before
    /// the first resolution; assignments after the folder is in use are ignored.
    /// </summary>
    public static string CacheRootFolder
    {
        get => _cacheRoot.IsValueCreated ? _cacheRoot.Value : _configuredCacheRoot;
        set
        {
            if (!_cacheRoot.IsValueCreated && !string.IsNullOrWhiteSpace(value))
            {
                _configuredCacheRoot = value;
            }
        }
    }

    /// <summary>
    /// How long a transient fetch failure (offline, timeout) suppresses retries
    /// for the same key within the current session.
    /// </summary>
    public static TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves the avatar image for the given email address honoring the
    /// Gravatar -> Favicon priority. Returns the absolute path of a cached
    /// image file, or null when the caller should keep showing initials.
    /// The token abandons only this caller's wait; a shared in-flight fetch
    /// keeps running for other awaiting callers.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string address,
        bool isGravatarEnabled,
        bool isFaviconEnabled,
        CancellationToken cancellationToken)
    {
        var email = NormalizeAddress(address);
        if (email is null) return null;

        if (isGravatarEnabled)
        {
            var gravatarUrl = $"https://www.gravatar.com/avatar/{Md5Hex(email)}?s={AvatarPixelSize}&d=404";
            var gravatarPath = await ResolveKindAsync($"{Sha256Hex(email)}-gravatar", gravatarUrl, cancellationToken).ConfigureAwait(false);
            if (gravatarPath is not null) return gravatarPath;
        }

        if (isFaviconEnabled)
        {
            var domain = GetPrimaryFaviconDomain(email);
            if (domain is not null)
            {
                var faviconUrl = $"https://www.google.com/s2/favicons?sz={AvatarPixelSize}&domain_url={domain}";
                return await ResolveKindAsync($"{Sha256Hex(domain)}-favicon", faviconUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Removes every cached avatar (memory and disk). The only way cached
    /// entries are ever refreshed.
    /// </summary>
    public static void ClearCache()
    {
        _memoryCache.Clear();

        if (!_cacheRoot.IsValueCreated || !Directory.Exists(_cacheRoot.Value)) return;

        foreach (var file in Directory.EnumerateFiles(_cacheRoot.Value)
                     .Where(file => Path.GetExtension(file) is ImageExtension or MissExtension))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A file may still be memory-mapped by a decoder; skip it.
            }
        }
    }

    private static async Task<string?> ResolveKindAsync(string cacheKey, string requestUrl, CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(cacheKey, out var entry))
        {
            if (entry.FilePath is not null)
            {
                if (File.Exists(entry.FilePath)) return entry.FilePath;

                // Cache file deleted externally; drop the stale entry and refetch.
                _memoryCache.TryRemove(cacheKey, out _);
            }
            else if (entry.RetryAfterUtc is null || DateTime.UtcNow < entry.RetryAfterUtc)
            {
                return null;
            }
            else
            {
                _memoryCache.TryRemove(cacheKey, out _);
            }
        }

        var imagePath = Path.Combine(_cacheRoot.Value, cacheKey + ImageExtension);
        var missPath = Path.Combine(_cacheRoot.Value, cacheKey + MissExtension);

        if (File.Exists(imagePath))
        {
            _memoryCache[cacheKey] = CacheEntry.Positive(imagePath);
            return imagePath;
        }

        if (File.Exists(missPath))
        {
            _memoryCache[cacheKey] = CacheEntry.PermanentNegative();
            return null;
        }

        var fetch = _inflightRequests.GetOrAdd(
            cacheKey,
            key => new Lazy<Task<string?>>(
                () => FetchAndStoreAsync(key, requestUrl, imagePath, missPath),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> FetchAndStoreAsync(string cacheKey, string requestUrl, string imagePath, string missPath)
    {
        try
        {
            using var response = await _httpClient.GetAsync(requestUrl).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Definitive "no image" answer (e.g. Gravatar d=404) — never retried.
                await File.WriteAllBytesAsync(missPath, []).ConfigureAwait(false);
                _memoryCache[cacheKey] = CacheEntry.PermanentNegative();
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                await File.WriteAllBytesAsync(missPath, []).ConfigureAwait(false);
                _memoryCache[cacheKey] = CacheEntry.PermanentNegative();
                return null;
            }

            // Write-then-move so concurrent readers never observe a torn file.
            var temporaryPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            File.Move(temporaryPath, imagePath, overwrite: true);

            _memoryCache[cacheKey] = CacheEntry.Positive(imagePath);
            return imagePath;
        }
        catch (Exception)
        {
            // Transient (offline, DNS, timeout, disk): back off in-memory only;
            // never persist so the next session retries immediately.
            _memoryCache[cacheKey] = CacheEntry.TransientNegative(DateTime.UtcNow + TransientRetryDelay);
            return null;
        }
        finally
        {
            _inflightRequests.TryRemove(cacheKey, out _);
        }
    }

    private static string? NormalizeAddress(string? address)
    {
        var email = address?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1) return null;

        return email;
    }

    private static string? GetPrimaryFaviconDomain(string email)
    {
        var host = email[(email.LastIndexOf('@') + 1)..];
        if (_excludedFaviconDomains.Contains(host)) return null;

        var hostParts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (hostParts.Length == 0) return null;

        return hostParts.Length > 1 ? string.Join('.', hostParts[^2..]) : host;
    }

    private static string Md5Hex(string value)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record CacheEntry(string? FilePath, DateTime? RetryAfterUtc)
    {
        public static CacheEntry Positive(string filePath) => new(filePath, null);

        public static CacheEntry PermanentNegative() => new(null, null);

        public static CacheEntry TransientNegative(DateTime retryAfterUtc) => new(null, retryAfterUtc);
    }
}
