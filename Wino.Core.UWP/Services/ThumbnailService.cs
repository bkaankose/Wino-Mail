using System;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using Gravatar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services;

public class ThumbnailService : IThumbnailService
{
    private readonly INativeAppService _nativeAppService;
    private readonly ConcurrentDictionary<string, object> _fileLocks = new();
    private readonly ConcurrentDictionary<string, string> _memoryCache = new();
    private readonly HttpClient _httpClient = new();

    public ThumbnailService(INativeAppService nativeAppService)
    {
        _nativeAppService = nativeAppService ?? throw new ArgumentNullException(nameof(nativeAppService), "Native app service cannot be null.");
    }

    public string GetHost(string address)
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

    public async Task<string> TryGetThumbnailsCacheDirectory()
    {
        return await _nativeAppService.GetThumbnailStoragePath();
    }

    private async Task<string> GetGravatarCacheFile(string email)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return Path.Combine(await TryGetThumbnailsCacheDirectory(), $"{hashString}.gravatar");
    }

    private async Task<string> GetFaviconCacheFile(string domain)
    {
        return Path.Combine(await TryGetThumbnailsCacheDirectory(), $"{domain.ToLowerInvariant()}.favicon");
    }

    public async Task<string> TryGetGravatarBase64Async(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        if (_memoryCache.TryGetValue($"gravatar:{email}", out var cached))
            return cached;
        var file = await GetGravatarCacheFile(email);
        if (File.Exists(file))
        {
            var base64 = await File.ReadAllTextAsync(file);
            _memoryCache.TryAdd($"gravatar:{email}", base64);
            return base64;
        }
        var fileLock = _fileLocks.GetOrAdd(file, _ => new object());
        lock (fileLock)
        {
            if (File.Exists(file))
            {
                var base64 = File.ReadAllText(file);
                _memoryCache.TryAdd($"gravatar:{email}", base64);
                return base64;
            }
        }
        try
        {
            var gravatarUrl = GravatarHelper.GetAvatarUrl(
                email,
                size: 128,
                defaultValue: GravatarAvatarDefault.Blank,
                withFileExtension: false).ToString();
            gravatarUrl = gravatarUrl.Replace("d=blank", "d=404");
            var response = await _httpClient.GetAsync(gravatarUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var base64 = Convert.ToBase64String(bytes);
                lock (fileLock)
                {
                    File.WriteAllText(file, base64);
                }
                _memoryCache.TryAdd($"gravatar:{email}", base64);
                return base64;
            }
        }
        catch { }
        return null;
    }

    public async Task<string> TryGetFaviconBase64Async(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        if (_memoryCache.TryGetValue($"favicon:{domain}", out var cached))
            return cached;
        var parts = domain.Split('.');
        for (int i = 0; i <= parts.Length - 2; i++)
        {
            var testDomain = string.Join(".", parts, i, parts.Length - i);
            var file = await GetFaviconCacheFile(testDomain);
            if (File.Exists(file))
            {
                var base64 = await File.ReadAllTextAsync(file);
                _memoryCache.TryAdd($"favicon:{testDomain}", base64);
                return base64;
            }
            var fileLock = _fileLocks.GetOrAdd(file, _ => new object());
            lock (fileLock)
            {
                if (File.Exists(file))
                {
                    var base64 = File.ReadAllText(file);
                    _memoryCache.TryAdd($"favicon:{testDomain}", base64);
                    return base64;
                }
            }
            var faviconUrl = $"https://icons.duckduckgo.com/ip3/{testDomain}.ico";
            var response = await _httpClient.GetAsync(faviconUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var base64 = Convert.ToBase64String(bytes);
                lock (fileLock)
                {
                    File.WriteAllText(file, base64);
                }
                _memoryCache.TryAdd($"favicon:{testDomain}", base64);
                return base64;
            }
        }
        return null;
    }
}
