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
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Storage.Streams;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Thumbnails;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Services;

public class ThumbnailService(
    IPreferencesService preferencesService,
    IThumbnailCacheService thumbnailCacheService,
    IApplicationConfiguration applicationConfiguration) : IThumbnailService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly IThumbnailCacheService _thumbnailCacheService = thumbnailCacheService;
    private readonly string _thumbnailCacheFolderPath = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, ThumbnailCacheFolderName);
    private static readonly HttpClient _httpClient = new();

    private const string ThumbnailCacheFolderName = "thumbnails";
    private const int AvatarCachePixelSize = 48;
    private const float JpegQuality = 0.78f;

    private readonly ConcurrentDictionary<string, ThumbnailCacheEntry> _cache = [];
    private readonly ConcurrentDictionary<string, Lazy<Task>> _requests = [];

    private sealed record ThumbnailCacheEntry(string? GravatarFileName, string? FaviconFileName);
    private sealed record NormalizedThumbnail(byte[] Data, string FileExtension);

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
        await _thumbnailCacheService.ClearAllThumbnailsAsync();

        ClearThumbnailFiles();
        EnsureThumbnailCacheFolder();
    }

    private async ValueTask<ThumbnailCacheEntry> GetThumbnailInternal(string email, bool awaitLoad)
    {
        if (_cache.TryGetValue(email, out var cached))
            return cached;

        var existingThumbnail = await _thumbnailCacheService.GetThumbnailAsync(email).ConfigureAwait(false);

        if (existingThumbnail != null)
        {
            var existingEntry = await GetExistingThumbnailEntryAsync(existingThumbnail).ConfigureAwait(false);
            if (existingEntry != null)
            {
                _cache[email] = existingEntry;
                return existingEntry;
            }

            await _thumbnailCacheService.DeleteThumbnailAsync(email).ConfigureAwait(false);
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

        await _thumbnailCacheService.SaveThumbnailAsync(new Thumbnail
        {
            Domain = email,
            GravatarFileName = gravatarFileName,
            FaviconFileName = faviconFileName,
            LastUpdated = DateTime.UtcNow
        }).ConfigureAwait(false);

        _cache[email] = new ThumbnailCacheEntry(gravatarFileName, faviconFileName);

        WeakReferenceMessenger.Default.Send(new ThumbnailAdded(email));
    }

    private async Task<string?> GetGravatarFileNameAsync(string email)
    {
        try
        {
            // SHA-256 of the normalized address per the Gravatar URL spec; d=404 so misses
            // return no image instead of a placeholder. Built by hand (the gravatar-dotnet
            // package was reflection-heavy and not trim-safe).
            var emailHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();
            var gravatarUrl = $"https://www.gravatar.com/avatar/{emailHash}?s={AvatarCachePixelSize}&d=404";
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

            var googleFaviconUrl = $"https://www.google.com/s2/favicons?sz={AvatarCachePixelSize}&domain_url={primaryDomain}";
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

            await _thumbnailCacheService.SaveThumbnailAsync(thumbnail).ConfigureAwait(false);
        }

        return new ThumbnailCacheEntry(gravatarFileName, faviconFileName);
    }

    private async Task<string?> SaveThumbnailFileAsync(string email, ThumbnailKind kind, byte[] imageData)
    {
        var normalizedThumbnail = await NormalizeAvatarAsync(imageData).ConfigureAwait(false);
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

    private static async Task<NormalizedThumbnail?> NormalizeAvatarAsync(byte[] imageData)
    {
        if (imageData.Length == 0)
        {
            return null;
        }

        using var sourceStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(sourceStream))
        {
            writer.WriteBytes(imageData);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        sourceStream.Seek(0);

        var canvasDevice = CanvasDevice.GetSharedDevice();
        using var sourceBitmap = await CanvasBitmap.LoadAsync(canvasDevice, sourceStream);
        if (sourceBitmap.SizeInPixels.Width == 0 || sourceBitmap.SizeInPixels.Height == 0)
        {
            return null;
        }

        var sourceWidth = sourceBitmap.SizeInPixels.Width;
        var sourceHeight = sourceBitmap.SizeInPixels.Height;
        var cropSize = Math.Min(sourceWidth, sourceHeight);
        var sourceRect = new Rect(
            (sourceWidth - cropSize) / 2d,
            (sourceHeight - cropSize) / 2d,
            cropSize,
            cropSize);

        using var renderTarget = new CanvasRenderTarget(canvasDevice, AvatarCachePixelSize, AvatarCachePixelSize, 96);
        using (var drawingSession = renderTarget.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            drawingSession.DrawImage(sourceBitmap, new Rect(0, 0, AvatarCachePixelSize, AvatarCachePixelSize), sourceRect);
        }

        var hasTransparency = sourceBitmap.GetPixelColors().Any(static color => color.A < byte.MaxValue);
        var fileFormat = hasTransparency ? CanvasBitmapFileFormat.Png : CanvasBitmapFileFormat.Jpeg;
        var fileExtension = hasTransparency ? ".png" : ".jpg";

        using var outputStream = new InMemoryRandomAccessStream();
        await renderTarget.SaveAsync(outputStream, fileFormat, JpegQuality);
        outputStream.Seek(0);

        using var reader = new DataReader(outputStream);
        if (outputStream.Size > int.MaxValue)
        {
            return null;
        }

        var outputLength = (uint)outputStream.Size;
        await reader.LoadAsync(outputLength);
        var outputBytes = new byte[(int)outputLength];
        reader.ReadBytes(outputBytes);

        return new NormalizedThumbnail(outputBytes, fileExtension);
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
