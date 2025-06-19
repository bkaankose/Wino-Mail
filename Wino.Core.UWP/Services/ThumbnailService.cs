using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using Gravatar;
using Windows.Networking.Connectivity;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.UWP.Services;

public class ThumbnailService(IPreferencesService preferencesService, IDatabaseService databaseService) : IThumbnailService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly IDatabaseService _databaseService = databaseService;
    private static readonly HttpClient _httpClient = new();
    private bool _isInitialized = false;

    private ConcurrentDictionary<string, (string graviton, string favicon)> _cache;
    private readonly ConcurrentDictionary<string, Task> _requests = [];

    public async ValueTask<string> GetAvatarThumbnail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        if (!_preferencesService.IsShowSenderPicturesEnabled)
            return null;

        if (!_isInitialized)
        {
            var thumbnailsList = await _databaseService.Connection.Table<Thumbnail>().ToListAsync();

            _cache = new ConcurrentDictionary<string, (string graviton, string favicon)>(
                thumbnailsList.ToDictionary(x => x.Domain, x => (x.Gravatar, x.Favicon)));
            _isInitialized = true;
        }

        var sanitizedEmail = email.Trim().ToLowerInvariant();

        var (gravatar, favicon) = GetThumbnailInternal(sanitizedEmail);

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
        await _databaseService.Connection.DeleteAllAsync<Thumbnail>();
    }

    public async Task PrefetchThumbnail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        var sanitizedEmail = email.Trim().ToLowerInvariant();

        await RequestNewThumbnail(sanitizedEmail);
    }

    private (string gravatar, string favicon) GetThumbnailInternal(string email)
    {
        if (_cache.TryGetValue(email, out var cached))
            return cached;

        // No network available, skip fetching Gravatar
        // Do not cache it, since network can be available later
        bool isInternetAvailable = GetIsInternetAvailable();

        if (!isInternetAvailable)
            return default;

        // Initialize thumbnail in a background, avoiding multiple requests for the same domain
        if (!_requests.TryGetValue(email, out var request))
        {
            _ = Task.Run(() => RequestNewThumbnail(email));
        }

        return default;

        static bool GetIsInternetAvailable()
        {
            var connection = NetworkInformation.GetInternetConnectionProfile();
            return connection != null && connection.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
    }

    private async Task RequestNewThumbnail(string email)
    {
        var gravatarBase64 = await GetGravatarBase64(email);
        var faviconBase64 = await GetFaviconBase64(email);

        await _databaseService.Connection.InsertOrReplaceAsync(new Thumbnail
        {
            Domain = email,
            Gravatar = gravatarBase64,
            Favicon = faviconBase64,
            LastUpdated = DateTime.UtcNow
        });
        _ = _cache.TryAdd(email, (gravatarBase64, faviconBase64));
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
            var primaryDomain = string.Join('.', host.Split('.')[^2..]);
            var faviconUrl = $"https://icons.duckduckgo.com/ip3/{primaryDomain}.ico";
            var response = await _httpClient.GetAsync(faviconUrl);
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
