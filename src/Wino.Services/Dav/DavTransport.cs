using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services.Dav;

public sealed class DavTransport : IDavTransport
{
    private const int MaximumRedirects = 5;
    private static readonly HttpRequestOptionsKey<DavCertificateContext> CertificateContextKey = new("Wino.DavCertificateContext");
    private static readonly ILogger Logger = Log.ForContext<DavTransport>();
    private readonly HttpClient _httpClient;
    private readonly IServerCertificateTrustService _certificateTrustService;

    public DavTransport(IServerCertificateTrustService certificateTrustService = null, HttpClient httpClient = null)
    {
        _certificateTrustService = certificateTrustService;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
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

        var template = await DavRequestTemplate.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var target = request.RequestUri;
        var certificateContext = await CreateCertificateContextAsync(authentication.EndpointTrust, target).ConfigureAwait(false);
        string digestAuthorization = null;
        var isBearerRefreshed = false;

        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            using var outgoing = template.Create(target);
            var bearerToken = authentication.BearerToken;
            ApplyAuthentication(outgoing, authentication, bearerToken, digestAuthorization);
            HttpResponseMessage response;

            if (certificateContext != null)
            {
                certificateContext.Failure = null;
                outgoing.Options.Set(CertificateContextKey, certificateContext);
            }

            try
            {
                response = await _httpClient.SendAsync(
                    outgoing,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var certificateFailure = certificateContext?.Failure
                    ?? ex.GetInnerExceptions().OfType<MailServerCertificateException>().FirstOrDefault()?.Failure;
                if (certificateFailure != null)
                    throw new MailServerCertificateException(certificateFailure, ex);

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

            // An access token can be revoked or expire early; one refresh and retry, then the 401 is handled as today.
            if (authentication.Kind == DavAuthenticationKind.Bearer &&
                response.StatusCode == HttpStatusCode.Unauthorized &&
                authentication.RefreshBearerTokenAsync is not null &&
                !isBearerRefreshed)
            {
                response.Dispose();
                isBearerRefreshed = true;
                authentication.BearerToken = await authentication.RefreshBearerTokenAsync(bearerToken, cancellationToken).ConfigureAwait(false);
                redirectCount--;
                continue;
            }

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;

            var redirected = DavUri.Resolve(target, response.Headers.Location);
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

    private async Task<DavCertificateContext> CreateCertificateContextAsync(DavEndpointTrust endpointTrust, Uri target)
    {
        if (endpointTrust == null || target.Scheme != Uri.UriSchemeHttps)
            return null;

        MailServerCertificateTrust storedTrust = null;
        if (endpointTrust.AccountId != Guid.Empty && _certificateTrustService != null)
        {
            try
            {
                storedTrust = await _certificateTrustService
                    .GetTrustAsync(endpointTrust.AccountId, endpointTrust.Protocol, target.Host, target.Port)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not read the stored {Protocol} certificate trust of account {AccountId}", endpointTrust.Protocol, endpointTrust.AccountId);
            }
        }

        return new DavCertificateContext(endpointTrust.Protocol, storedTrust, endpointTrust.TransientTrust);
    }

    // Requests without a trust context (relay, discovery lookups, anonymous downloads) keep the system decision.
    private static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        if (request?.RequestUri == null ||
            !request.Options.TryGetValue(CertificateContextKey, out var context) ||
            context == null)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        context.Failure = ServerCertificateValidator.Evaluate(
            certificate,
            chain,
            sslPolicyErrors,
            context.Protocol,
            request.RequestUri.Host,
            request.RequestUri.Port,
            context.StoredTrust,
            context.TransientTrust);

        return context.Failure == null;
    }

    private static void ApplyAuthentication(HttpRequestMessage request, DavAuthenticationProfile authentication, string bearerToken, string digestAuthorization)
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
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    string.IsNullOrWhiteSpace(authentication.BearerScheme) ? "Bearer" : authentication.BearerScheme, bearerToken);
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

    private sealed class DavCertificateContext(
        MailServerProtocol protocol,
        MailServerCertificateTrust storedTrust,
        MailServerCertificateTrust transientTrust)
    {
        public MailServerProtocol Protocol { get; } = protocol;
        public MailServerCertificateTrust StoredTrust { get; } = storedTrust;
        public MailServerCertificateTrust TransientTrust { get; } = transientTrust;
        public MailServerCertificateFailure Failure { get; set; }
    }

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
