using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Gravatar;
using Microsoft.EntityFrameworkCore;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;
using Wino.Services;

namespace Wino.Core.WinUI.Services;

public class ThumbnailService(IPreferencesService preferencesService, IDbContextFactory<WinoDbContext> contextFactory) : IThumbnailService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly IDbContextFactory<WinoDbContext> _contextFactory = contextFactory;
    private static readonly HttpClient _httpClient = new();
    private bool _isInitialized = false;

    private ConcurrentDictionary<string, (string graviton, string favicon)> _cache;
    private readonly ConcurrentDictionary<string, Task> _requests = [];

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

    public async ValueTask<string> GetThumbnailAsync(string email, bool awaitLoad = false)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        if (!_preferencesService.IsShowSenderPicturesEnabled)
            return null;

        if (!_isInitialized)
        {
            using var context = _contextFactory.CreateDbContext();
            var thumbnailsList = await context.Thumbnails.ToListAsync();

            _cache = new ConcurrentDictionary<string, (string graviton, string favicon)>(
                thumbnailsList.ToDictionary(x => x.Address, x => (x.Gravatar, x.Favicon)));
            _isInitialized = true;
        }

        var sanitizedEmail = email.Trim().ToLowerInvariant();

        var (gravatar, favicon) = await GetThumbnailInternal(sanitizedEmail, awaitLoad);

        if (_preferencesService.IsGravatarEnabled && !string.IsNullOrEmpty(gravatar))
        {
            return gravatar;
        }

        if (_preferencesService.IsFaviconEnabled && !string.IsNullOrEmpty(favicon))
        {
            return favicon;
        }

        return null;
    }

    public async Task ClearCache()
    {
        _cache?.Clear();
        _requests.Clear();
        
        using var context = _contextFactory.CreateDbContext();
        await context.Thumbnails.ExecuteDeleteAsync();
    }

    private async ValueTask<(string gravatar, string favicon)> GetThumbnailInternal(string email, bool awaitLoad)
    {
        if (_cache.TryGetValue(email, out var cached))
            return cached;

        // No network available, skip fetching Gravatar
        // Do not cache it, since network can be available later
        //bool isInternetAvailable = GetIsInternetAvailable();

        //if (!isInternetAvailable)
        //    return default;

        if (!_requests.TryGetValue(email, out var request))
        {
            request = Task.Run(() => RequestNewThumbnail(email));
            _requests[email] = request;
        }

        if (awaitLoad)
        {
            await request;
            _cache.TryGetValue(email, out cached);
            return cached;
        }

        return default;

        //static bool GetIsInternetAvailable()
        //{
        //    var connection = NetworkInformation.GetInternetConnectionProfile();
        //    return connection != null && connection.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        //}
    }

    private async Task RequestNewThumbnail(string email)
    {
        var gravatarBase64 = await GetGravatarBase64(email);
        var faviconBase64 = await GetFaviconBase64(email);

        using var context = _contextFactory.CreateDbContext();
        
        // Try to find existing thumbnail
        var existingThumbnail = await context.Thumbnails
            .FirstOrDefaultAsync(t => t.Address == email);

        if (existingThumbnail != null)
        {
            // Update existing
            existingThumbnail.Gravatar = gravatarBase64;
            existingThumbnail.Favicon = faviconBase64;
            existingThumbnail.LastUpdated = DateTime.UtcNow;
        }
        else
        {
            // Insert new
            context.Thumbnails.Add(new Thumbnail
            {
                Id = Guid.NewGuid(),
                Address = email,
                Gravatar = gravatarBase64,
                Favicon = faviconBase64,
                LastUpdated = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
        _ = _cache.TryAdd(email, (gravatarBase64, faviconBase64));

        WeakReferenceMessenger.Default.Send(new ThumbnailAdded(email));
    }

    private static async Task<string> GetGravatarBase64(string email)
    {
        try
        {
            var gravatarUrl = GravatarHelper.GetAvatarUrl(
                email,
                size: 128,
                defaultValue: GravatarAvatarDefault.Blank,
                withFileExtension: false).ToString().Replace("d=blank", "d=404");
            var response = await _httpClient.GetAsync(gravatarUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = response.Content.ReadAsByteArrayAsync().Result;
                return Convert.ToBase64String(bytes);
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> GetFaviconBase64(string email)
    {
        try
        {
            var host = GetHost(email);

            if (string.IsNullOrEmpty(host))
                return null;

            // Do not fetch favicon for specific default domains of major platforms
            if (_excludedFaviconDomains.Contains(host, StringComparer.OrdinalIgnoreCase))
                return null;

            var primaryDomain = string.Join('.', host.Split('.')[^2..]);

            var googleFaviconUrl = $"https://www.google.com/s2/favicons?sz=128&domain_url={primaryDomain}";
            var response = await _httpClient.GetAsync(googleFaviconUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = response.Content.ReadAsByteArrayAsync().Result;
                return Convert.ToBase64String(bytes);
            }
        }
        catch { }
        return null;
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
