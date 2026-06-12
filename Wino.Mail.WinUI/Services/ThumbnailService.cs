using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Gravatar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Thumbnails;
using Wino.Messaging.UI;
using Wino.Services;

namespace Wino.Mail.WinUI.Services;

public class ThumbnailService(
    IPreferencesService preferencesService,
    IDatabaseService databaseService,
    IApplicationConfiguration applicationConfiguration) : IThumbnailService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly IDatabaseService _databaseService = databaseService;
    private readonly string _thumbnailCacheFolderPath = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, ThumbnailCacheFolderName);
    private static readonly HttpClient _httpClient = new();

    private const string ThumbnailCacheFolderName = "thumbnails";

    private readonly ConcurrentDictionary<string, ThumbnailCacheEntry> _cache = [];
    private readonly ConcurrentDictionary<string, Lazy<Task>> _requests = [];

    private sealed record ThumbnailCacheEntry(string? GravatarFileName, string? FaviconFileName);

    private static readonly List<string> _excludedFaviconDomains = [
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
        "rediffmail.com"
        ];

    public async ValueTask<ThumbnailResult?> GetThumbnailAsync(string email, bool awaitLoad = false)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        if (!_preferencesService.IsShowSenderPicturesEnabled)
            return null;

        var sanitizedEmail = email.Trim().ToLowerInvariant();

        var thumbnail = await GetThumbnailInternal(sanitizedEmail, awaitLoad).ConfigureAwait(false);

        if (_preferencesService.IsGravatarEnabled)
        {
            var gravatarResult = BuildThumbnailResult(thumbnail.GravatarFileName, ThumbnailKind.Gravatar);
            if (gravatarResult != null)
                return gravatarResult;
        }

        if (_preferencesService.IsFaviconEnabled)
        {
            var faviconResult = BuildThumbnailResult(thumbnail.FaviconFileName, ThumbnailKind.Favicon);
            if (faviconResult != null)
                return faviconResult;
        }

        return null;
    }

    public async Task ClearCache()
    {
        _cache.Clear();
        _requests.Clear();
        await _databaseService.Connection.DeleteAllAsync<Thumbnail>();

        ClearThumbnailFiles();
        EnsureThumbnailCacheFolder();
    }

    private async ValueTask<ThumbnailCacheEntry> GetThumbnailInternal(string email, bool awaitLoad)
    {
        if (_cache.TryGetValue(email, out var cached))
            return cached;

        var existingThumbnail = await _databaseService.Connection
            .Table<Thumbnail>()
            .Where(thumbnail => thumbnail.Domain == email)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (existingThumbnail != null)
        {
            var existingEntry = await GetExistingThumbnailEntryAsync(existingThumbnail).ConfigureAwait(false);
            if (existingEntry != null)
            {
                _cache[email] = existingEntry;
                return existingEntry;
            }

            await _databaseService.Connection
                .ExecuteAsync($"DELETE FROM {nameof(Thumbnail)} WHERE {nameof(Thumbnail.Domain)} = ?", email)
                .ConfigureAwait(false);
        }

        // No network available, skip fetching Gravatar
        // Do not cache it, since network can be available later
        //bool isInternetAvailable = GetIsInternetAvailable();

        //if (!isInternetAvailable)
        //    return default;

        var request = _requests.GetOrAdd(email, static (key, state) =>
            new Lazy<Task>(
                () => ((ThumbnailService)state!).RequestNewThumbnailAndCleanupAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication), this);

        _ = request.Value;

        if (awaitLoad)
        {
            await request.Value.ConfigureAwait(false);
            _cache.TryGetValue(email, out cached);
            return cached ?? new ThumbnailCacheEntry(null, null);
        }

        return new ThumbnailCacheEntry(null, null);

        //static bool GetIsInternetAvailable()
        //{
        //    var connection = NetworkInformation.GetInternetConnectionProfile();
        //    return connection != null && connection.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        //}
    }

    private async Task RequestNewThumbnailAndCleanupAsync(string email)
    {
        try
        {
            await RequestNewThumbnailAsync(email).ConfigureAwait(false);
        }
        finally
        {
            _ = _requests.TryRemove(email, out _);
        }
    }

    private async Task RequestNewThumbnailAsync(string email)
    {
        var gravatarFileName = await GetGravatarFileNameAsync(email).ConfigureAwait(false);
        var faviconFileName = await GetFaviconFileNameAsync(email).ConfigureAwait(false);

        await _databaseService.Connection.InsertOrReplaceAsync(new Thumbnail
        {
            Domain = email,
            GravatarFileName = gravatarFileName,
            FaviconFileName = faviconFileName,
            LastUpdated = DateTime.UtcNow
        }, typeof(Thumbnail));

        _cache[email] = new ThumbnailCacheEntry(gravatarFileName, faviconFileName);

        WeakReferenceMessenger.Default.Send(new ThumbnailAdded(email));
    }

    private async Task<string?> GetGravatarFileNameAsync(string email)
    {
        try
        {
            var gravatarUrl = GravatarHelper.GetAvatarUrl(
                email,
                size: ThumbnailImageProcessor.AvatarCachePixelSize,
                defaultValue: GravatarAvatarDefault.Blank,
                withFileExtension: false).ToString().Replace("d=blank", "d=404");
            using var response = await _httpClient.GetAsync(gravatarUrl).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return await SaveThumbnailFileAsync(email, ThumbnailKind.Gravatar, bytes).ConfigureAwait(false);
            }
        }
        catch { }
        return null;
    }

    private async Task<string?> GetFaviconFileNameAsync(string email)
    {
        try
        {
            var host = GetHost(email);

            if (string.IsNullOrEmpty(host))
                return null;

            // Do not fetch favicon for specific default domains of major platforms
            if (_excludedFaviconDomains.Contains(host, StringComparer.OrdinalIgnoreCase))
                return null;

            var hostParts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var primaryDomain = hostParts.Length > 1
                ? string.Join('.', hostParts[^2..])
                : host;

            var googleFaviconUrl = $"https://www.google.com/s2/favicons?sz={ThumbnailImageProcessor.AvatarCachePixelSize}&domain_url={primaryDomain}";
            using var response = await _httpClient.GetAsync(googleFaviconUrl).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return await SaveThumbnailFileAsync(email, ThumbnailKind.Favicon, bytes).ConfigureAwait(false);
            }
        }
        catch { }
        return null;
    }

    private async Task<ThumbnailCacheEntry?> GetExistingThumbnailEntryAsync(Thumbnail thumbnail)
    {
        var hasGravatarFileName = !string.IsNullOrWhiteSpace(thumbnail.GravatarFileName);
        var hasFaviconFileName = !string.IsNullOrWhiteSpace(thumbnail.FaviconFileName);

        if (!hasGravatarFileName && !hasFaviconFileName)
            return new ThumbnailCacheEntry(null, null);

        var gravatarFileName = hasGravatarFileName && File.Exists(BuildThumbnailPath(thumbnail.GravatarFileName))
            ? thumbnail.GravatarFileName
            : null;

        var faviconFileName = hasFaviconFileName && File.Exists(BuildThumbnailPath(thumbnail.FaviconFileName))
            ? thumbnail.FaviconFileName
            : null;

        if (gravatarFileName == null && faviconFileName == null)
            return null;

        if (thumbnail.GravatarFileName != gravatarFileName || thumbnail.FaviconFileName != faviconFileName)
        {
            thumbnail.GravatarFileName = gravatarFileName;
            thumbnail.FaviconFileName = faviconFileName;
            thumbnail.LastUpdated = DateTime.UtcNow;

            await _databaseService.Connection.InsertOrReplaceAsync(thumbnail, typeof(Thumbnail)).ConfigureAwait(false);
        }

        return new ThumbnailCacheEntry(gravatarFileName, faviconFileName);
    }

    private async Task<string?> SaveThumbnailFileAsync(string email, ThumbnailKind kind, byte[] imageData)
    {
        var normalizedThumbnail = ThumbnailImageProcessor.NormalizeAvatar(imageData);
        if (normalizedThumbnail == null)
            return null;

        EnsureThumbnailCacheFolder();

        var filePrefix = $"{ComputeEmailHash(email)}-{kind.ToString().ToLowerInvariant()}";
        var fileName = $"{filePrefix}{normalizedThumbnail.FileExtension}";
        var filePath = BuildThumbnailPath(fileName);

        DeleteStaleThumbnailVariants(filePrefix, fileName);

        await File.WriteAllBytesAsync(filePath, normalizedThumbnail.Data).ConfigureAwait(false);

        return fileName;
    }

    private ThumbnailResult? BuildThumbnailResult(string? fileName, ThumbnailKind kind)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var filePath = BuildThumbnailPath(fileName);
        if (!File.Exists(filePath))
            return null;

        return new ThumbnailResult(
            filePath,
            $"ms-appdata:///local/{ThumbnailCacheFolderName}/{Uri.EscapeDataString(fileName)}",
            kind);
    }

    private void EnsureThumbnailCacheFolder()
    {
        Directory.CreateDirectory(_thumbnailCacheFolderPath);
    }

    private string BuildThumbnailPath(string fileName) => Path.Combine(_thumbnailCacheFolderPath, fileName);

    private void DeleteStaleThumbnailVariants(string filePrefix, string currentFileName)
    {
        if (!Directory.Exists(_thumbnailCacheFolderPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(_thumbnailCacheFolderPath, $"{filePrefix}.*"))
        {
            if (!Path.GetFileName(filePath).Equals(currentFileName, StringComparison.OrdinalIgnoreCase))
                File.Delete(filePath);
        }
    }

    private void ClearThumbnailFiles()
    {
        if (!Directory.Exists(_thumbnailCacheFolderPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(_thumbnailCacheFolderPath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // A visible avatar may still have the file open; skip it and let future cache writes replace it.
            }
        }
    }

    private static string ComputeEmailHash(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetHost(string email)
    {
        if (!string.IsNullOrEmpty(email) && email.Contains('@'))
        {
            var split = email.Split('@');
            if (split.Length >= 2 && !string.IsNullOrEmpty(split[1]))
            {
                try { return new MailAddress(email).Host; } catch { }
            }
        }
        return string.Empty;
    }
}
