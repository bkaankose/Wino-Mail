using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Gravatar;
using Windows.Networking.Connectivity;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services;

public class ThumbnailService(INativeAppService nativeAppService, IPreferencesService preferencesService) : IThumbnailService
{
    private readonly INativeAppService _nativeAppService = nativeAppService;
    private readonly IPreferencesService _preferencesService = preferencesService;
    private static readonly HttpClient _httpClient = new();

    private static readonly ConcurrentDictionary<string, Lock> _fileLocks = [];
    private static readonly ConcurrentDictionary<string, string> _memoryCache = [];

    public async Task<string> GetAvatarThumbnail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        if (!_preferencesService.IsShowSenderPicturesEnabled)
            return null;

        var sanitizedEmail = email.Trim().ToLowerInvariant();

        if (_preferencesService.IsGravatarEnabled)
        {
            var gravatar = await GetThumbnailInternal(sanitizedEmail, "gravatar", static x => GravatarHelper.GetAvatarUrl(
                x,
                size: 128,
                defaultValue: GravatarAvatarDefault.Blank,
                withFileExtension: false).ToString().Replace("d=blank", "d=404"));

            if (!string.IsNullOrEmpty(gravatar))
                return gravatar;
        }

        if (_preferencesService.IsFaviconEnabled)
        {
            var favicon = await GetThumbnailInternal(sanitizedEmail, "favicon", static x =>
            {
                var host = GetHost(x);
                if (string.IsNullOrEmpty(host))
                    return null;

                var primaryDomain = string.Join('.', host.Split('.')[^2..]);

                return $"https://icons.duckduckgo.com/ip3/{primaryDomain}.ico";
            });

            if (!string.IsNullOrEmpty(favicon))
                return favicon;
        }

        return null;
    }

    private async Task<string> GetThumbnailInternal(string email, string type, Func<string, string> getUrl)
    {
        var host = GetHost(email);
        if (_memoryCache.TryGetValue($"{type}:{host}", out var cached))
            return cached;

        var filePath = Path.Combine(await _nativeAppService.GetThumbnailStoragePath(), $"{host}.{type}");

        if (File.Exists(filePath))
        {
            var base64 = await File.ReadAllTextAsync(filePath);
            _ = _memoryCache.TryAdd($"{type}:{host}", base64);
            return base64;
        }

        var fileLock = _fileLocks.GetOrAdd(filePath, _ => new Lock());
        try
        {
            // No network available, skip fetching Gravatar
            // Do not cache it, since network can be available later
            bool isInternetAvailable = GetIsInternetAvailable();

            if (!isInternetAvailable)
                return null;

            var url = getUrl(email);
            if (string.IsNullOrEmpty(url))
                return null;

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var base64 = Convert.ToBase64String(bytes);
                lock (fileLock)
                {
                    File.WriteAllText(filePath, base64);
                }
                _memoryCache.TryAdd($"{type}:{host}", base64);
                return base64;
            }
            // Cache null to avoid repeated requests for this email during the session
            _memoryCache.TryAdd($"{type}:{host}", string.Empty);
            lock (fileLock)
            {
                // Ensure we create the file to prevent future requests
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, string.Empty);
                }
            }
        }
        catch { }
        return null;

        static bool GetIsInternetAvailable()
        {
            var connection = NetworkInformation.GetInternetConnectionProfile();
            return connection != null && connection.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
    }

    private static string GetHost(string address)
    {
        if (!string.IsNullOrEmpty(address) && address.Contains('@'))
        {
            var split = address.Split('@');
            if (split.Length >= 2 && !string.IsNullOrEmpty(split[1]))
            {
                try { return new MailAddress(address).Host; } catch { }
            }
        }
        return string.Empty;
    }
}
