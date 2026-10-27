using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Wino.Editor;

public static class RemoteImageFetcher
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const long MaxMessageBytes = 25L * 1024 * 1024;

    private const int CacheCapacity = 64;
    private const long CacheCharacterCapacity = 8L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp",
        "image/bmp", "image/avif", "image/x-icon", "image/vnd.microsoft.icon"
    };

    private static readonly HttpClient Client = CreateClient();
    private static readonly Lock CacheLock = new();
    private static readonly LinkedList<CacheEntry> CacheOrder = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = new(StringComparer.Ordinal);
    private static long _cacheCharacters;

    public static bool IsFetchableUri(string? value, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && IsHttp(uri);

    public static async Task<RemoteImageFetchResult?> FetchAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsHttp(uri)) return null;
        long limit = Math.Min(maxBytes, MaxImageBytes);
        if (limit <= 0) return null;

        string key = uri.AbsoluteUri;
        if (TryGetCached(key, limit, out RemoteImageFetchResult? cached)) return cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/*");
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is not { } finalUri || !IsHttp(finalUri))
                return null;

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !AllowedMediaTypes.Contains(mediaType)) return null;
            if (response.Content.Headers.ContentLength is long declared && declared > limit) return null;

            byte[]? content = await ReadLimitedAsync(response.Content, limit, timeout.Token).ConfigureAwait(false);
            if (content is null || content.Length == 0) return null;

            var result = new RemoteImageFetchResult(
                $"data:{mediaType.ToLowerInvariant()};base64,{Convert.ToBase64String(content)}",
                content.Length);
            AddToCache(key, result);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool TryGetCached(string key, long limit, out RemoteImageFetchResult? result)
    {
        lock (CacheLock)
        {
            if (CacheIndex.TryGetValue(key, out var node) && node.Value.Result.ByteCount <= limit)
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                result = node.Value.Result;
                return true;
            }
        }

        result = null;
        return false;
    }

    private static void AddToCache(string key, RemoteImageFetchResult result)
    {
        lock (CacheLock)
        {
            if (CacheIndex.TryGetValue(key, out var existing))
            {
                CacheOrder.Remove(existing);
                CacheIndex.Remove(key);
                _cacheCharacters -= existing.Value.Result.DataUri.Length;
            }

            if (result.DataUri.Length > CacheCharacterCapacity) return;

            CacheIndex[key] = CacheOrder.AddFirst(new CacheEntry(key, result));
            _cacheCharacters += result.DataUri.Length;

            while ((CacheIndex.Count > CacheCapacity || _cacheCharacters > CacheCharacterCapacity) && CacheOrder.Last is { } oldest)
            {
                CacheOrder.RemoveLast();
                CacheIndex.Remove(oldest.Value.Key);
                _cacheCharacters -= oldest.Value.Result.DataUri.Length;
            }
        }
    }

    // Mail decides which URL is fetched, so every connection, redirects included, may only reach
    // public addresses. Otherwise a message could make the desktop process call LAN or loopback services.
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);

        foreach (var address in addresses)
        {
            if (!IsPublicAddress(address))
                continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }

        throw new HttpRequestException("The image host does not resolve to a public address.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = address.GetAddressBytes()[0];
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (first & 0xFE) == 0xFC);
        }

        return false;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false,
            ConnectCallback = ConnectToPublicAddressAsync
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
    }

    private sealed record CacheEntry(string Key, RemoteImageFetchResult Result);
}

public sealed record RemoteImageFetchResult(string DataUri, int ByteCount);
