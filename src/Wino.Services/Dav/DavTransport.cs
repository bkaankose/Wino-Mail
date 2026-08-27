using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.Dav;

public sealed class DavTransport : IDavTransport
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient _httpClient;

    public DavTransport(HttpClient httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        DavAuthenticationProfile authentication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        ArgumentNullException.ThrowIfNull(authentication);

        if (authentication.Kind == DavAuthenticationKind.Basic && request.RequestUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Basic DAV authentication requires HTTPS.");

        var template = await DavRequestTemplate.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var target = request.RequestUri;
        string digestAuthorization = null;

        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            using var outgoing = template.Create(target);
            ApplyAuthentication(outgoing, authentication, digestAuthorization);
            HttpResponseMessage response;

            try
            {
                response = await _httpClient.SendAsync(
                    outgoing,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new DavRequestException(0, Translator.DavError_Network, innerException: ex);
            }

            if (authentication.Kind == DavAuthenticationKind.Digest &&
                response.StatusCode == HttpStatusCode.Unauthorized && digestAuthorization is null)
            {
                digestAuthorization = CreateDigestAuthorization(response, outgoing, authentication);
                response.Dispose();
                if (digestAuthorization is not null)
                    continue;
                throw new UnauthorizedAccessException("The DAV server did not return a supported Digest challenge.");
            }

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;

            var redirected = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(target, response.Headers.Location);
            response.Dispose();

            if (!SameOrigin(target, redirected))
                throw new DavRequestException(0, Translator.DavError_Network);
            if (target.Scheme == Uri.UriSchemeHttps && redirected.Scheme != Uri.UriSchemeHttps)
                throw new DavRequestException(0, Translator.DavError_Network);

            target = redirected;
            digestAuthorization = null;
        }

        throw new DavRequestException(0, Translator.DavError_Network);
    }

    private static void ApplyAuthentication(HttpRequestMessage request, DavAuthenticationProfile authentication, string digestAuthorization)
    {
        switch (authentication.Kind)
        {
            case DavAuthenticationKind.Basic:
                var credentialBytes = Encoding.UTF8.GetBytes($"{authentication.Username}:{authentication.Password}");
                try
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(credentialBytes);
                }
                break;
            case DavAuthenticationKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.BearerToken);
                break;
            case DavAuthenticationKind.Digest when !string.IsNullOrWhiteSpace(digestAuthorization):
                request.Headers.TryAddWithoutValidation("Authorization", digestAuthorization);
                break;
        }
    }

    private static string CreateDigestAuthorization(
        HttpResponseMessage response,
        HttpRequestMessage request,
        DavAuthenticationProfile authentication)
    {
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(value =>
            value.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
        if (challenge?.Parameter is null)
            return null;

        var values = ParseChallenge(challenge.Parameter);
        if (!values.TryGetValue("realm", out var realm) || !values.TryGetValue("nonce", out var nonce))
            return null;

        var algorithm = values.GetValueOrDefault("algorithm") ?? "MD5";
        var qop = values.GetValueOrDefault("qop")?.Split(',').Select(value => value.Trim())
            .FirstOrDefault(value => value.Equals("auth", StringComparison.OrdinalIgnoreCase));
        var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        const string nonceCount = "00000001";
        var uri = request.RequestUri.PathAndQuery;
        var ha1 = DigestHash($"{authentication.Username}:{realm}:{authentication.Password}", algorithm);
        var ha2 = DigestHash($"{request.Method.Method}:{uri}", algorithm);
        var responseHash = string.IsNullOrWhiteSpace(qop)
            ? DigestHash($"{ha1}:{nonce}:{ha2}", algorithm)
            : DigestHash($"{ha1}:{nonce}:{nonceCount}:{cnonce}:{qop}:{ha2}", algorithm);

        var builder = new StringBuilder("Digest ");
        builder.Append($"username=\"{EscapeQuoted(authentication.Username)}\", realm=\"{EscapeQuoted(realm)}\", nonce=\"{EscapeQuoted(nonce)}\", uri=\"{EscapeQuoted(uri)}\", response=\"{responseHash}\", algorithm={algorithm}");
        if (!string.IsNullOrWhiteSpace(qop))
            builder.Append($", qop={qop}, nc={nonceCount}, cnonce=\"{cnonce}\"");
        if (values.TryGetValue("opaque", out var opaque))
            builder.Append($", opaque=\"{EscapeQuoted(opaque)}\"");
        return builder.ToString();
    }

    private static string DigestHash(string value, string algorithm)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase)
            ? SHA256.HashData(bytes)
            : MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseChallenge(string challenge)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < challenge.Length)
        {
            while (index < challenge.Length && (char.IsWhiteSpace(challenge[index]) || challenge[index] == ',')) index++;
            var equals = challenge.IndexOf('=', index);
            if (equals < 0) break;
            var name = challenge[index..equals].Trim();
            index = equals + 1;
            string value;
            if (index < challenge.Length && challenge[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < challenge.Length && challenge[index] != '"')
                {
                    if (challenge[index] == '\\' && index + 1 < challenge.Length) index++;
                    builder.Append(challenge[index++]);
                }
                if (index < challenge.Length) index++;
                value = builder.ToString();
            }
            else
            {
                var comma = challenge.IndexOf(',', index);
                if (comma < 0) comma = challenge.Length;
                value = challenge[index..comma].Trim();
                index = comma;
            }
            values[name] = value;
        }
        return values;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri left, Uri right)
        => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static string EscapeQuoted(string value) => value?.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class DavRequestTemplate
    {
        private readonly HttpMethod _method;
        private readonly Version _version;
        private readonly HttpVersionPolicy _versionPolicy;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _headers;
        private readonly byte[] _content;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _contentHeaders;

        private DavRequestTemplate(
            HttpMethod method,
            Version version,
            HttpVersionPolicy versionPolicy,
            List<KeyValuePair<string, IEnumerable<string>>> headers,
            byte[] content,
            List<KeyValuePair<string, IEnumerable<string>>> contentHeaders)
        {
            _method = method;
            _version = version;
            _versionPolicy = versionPolicy;
            _headers = headers;
            _content = content;
            _contentHeaders = contentHeaders;
        }

        public static async Task<DavRequestTemplate> CreateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new DavRequestTemplate(
                request.Method,
                request.Version,
                request.VersionPolicy,
                request.Headers.Where(header => !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).ToList(),
                content,
                request.Content?.Headers.ToList() ?? []);
        }

        public HttpRequestMessage Create(Uri target)
        {
            var request = new HttpRequestMessage(_method, target)
            {
                Version = _version,
                VersionPolicy = _versionPolicy
            };
            foreach (var header in _headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (_content is not null)
            {
                request.Content = new ByteArrayContent(_content);
                foreach (var header in _contentHeaders)
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return request;
        }
    }
}
