using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Wino.Core.Domain.Models.AutoDiscovery;

namespace Wino.Core.Services;

/// <summary>
/// Outlook (POX) Autodiscover, as published by Exchange, SmarterMail, MDaemon, Kerio and other groupware servers
/// that serve no Thunderbird autoconfig file. Candidates follow the MS-OXDISCO order: the domain itself, the
/// <c>autodiscover</c> host, the HTTP redirect on that host, then the <c>_autodiscover._tcp</c> SRV record.
/// </summary>
public partial class AutoDiscoveryService
{
    private const string OutlookAutodiscoverPath = "/autodiscover/autodiscover.xml";
    private const string OutlookRequestSchema = "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006";
    private const string OutlookResponseSchema = "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a";
    private const int MaxOutlookAutodiscoverRedirects = 10;

    // Redirects are read by hand: an automatic redirect turns the POST into a GET and loses the request body.
    private readonly HttpClient _outlookAutodiscoverClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly Dictionary<string, OutlookAutodiscoverResult> _outlookAutodiscoverCache = new(IgnoreCase);
    private readonly object _outlookAutodiscoverCacheLock = new();

    private async Task<AutoDiscoverySettings> TryGetOutlookAutodiscoverSettingsAsync(
        string domain,
        string email,
        string password,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        var result = await GetOutlookAutodiscoverResultAsync(email, password, cancellationToken).ConfigureAwait(false);
        if (result == null)
            return null;

        var incoming = SelectBestServerSetting(result.Servers.Where(s => IgnoreCase.Equals(s.Protocol, incomingProtocol)).ToList());
        var outgoing = SelectBestServerSetting(result.Servers.Where(s => IgnoreCase.Equals(s.Protocol, "SMTP")).ToList());

        if (incoming == null || outgoing == null)
            return null;

        return new AutoDiscoverySettings
        {
            Domain = domain,
            Settings = [incoming, outgoing]
        };
    }

    private async Task<Uri> TryGetOutlookAutodiscoverDavUriAsync(string email, CancellationToken cancellationToken)
    {
        var result = await GetOutlookAutodiscoverResultAsync(email, password: null, cancellationToken).ConfigureAwait(false);
        return result?.DavUri;
    }

    private async Task<OutlookAutodiscoverResult> GetOutlookAutodiscoverResultAsync(string email, string password, CancellationToken cancellationToken)
    {
        lock (_outlookAutodiscoverCacheLock)
        {
            if (_outlookAutodiscoverCache.TryGetValue(email, out var cached))
                return cached;
        }

        var result = await DiscoverOutlookSettingsAsync(email, password, cancellationToken).ConfigureAwait(false);

        // A run without a password may have stopped at a sign-in challenge that a later run with one passes.
        if (result != null || !string.IsNullOrEmpty(password))
        {
            lock (_outlookAutodiscoverCacheLock)
            {
                _outlookAutodiscoverCache[email] = result;
            }
        }

        return result;
    }

    private async Task<OutlookAutodiscoverResult> DiscoverOutlookSettingsAsync(string originalEmail, string password, CancellationToken cancellationToken)
    {
        if (!TryGetEmailParts(originalEmail, out _, out var credentialDomain))
            return null;

        var context = new OutlookAutodiscoverContext(originalEmail, password, credentialDomain);
        var email = originalEmail;

        while (TryGetEmailParts(email, out var localPart, out var domain))
        {
            OutlookAutodiscoverResponse response = null;

            await foreach (var candidate in GetOutlookAutodiscoverCandidatesAsync(domain, cancellationToken).ConfigureAwait(false))
            {
                response = await PostOutlookAutodiscoverAsync(candidate, email, localPart, domain, context, cancellationToken).ConfigureAwait(false);
                if (response != null)
                    break;
            }

            if (response == null)
                return null;

            if (response.Result != null)
            {
                Logger.Information("Outlook Autodiscover for {Domain} returned {Count} server setting(s)", domain, response.Result.Servers.Count);
                return response.Result;
            }

            if (response.RedirectAddress == null || !context.TryUseRedirect())
                return null;

            Logger.Debug("Outlook Autodiscover for {Domain} redirected to another address", domain);
            email = response.RedirectAddress;
        }

        return null;
    }

    private async IAsyncEnumerable<Uri> GetOutlookAutodiscoverCandidatesAsync(
        string domain,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new Uri($"https://{domain}{OutlookAutodiscoverPath}");
        yield return new Uri($"https://autodiscover.{domain}{OutlookAutodiscoverPath}");

        var redirected = await GetOutlookHttpRedirectTargetAsync(domain, cancellationToken).ConfigureAwait(false);
        if (redirected != null)
            yield return redirected;

        var srvRecords = (await QueryDnsAsync($"_autodiscover._tcp.{domain}", "SRV", cancellationToken).ConfigureAwait(false))
            .Select(ParseSrvRecord)
            .Where(record => record != null && record.Port is > 0 and <= 65535)
            .OrderBy(record => record.Priority)
            .ThenByDescending(record => record.Weight);

        foreach (var record in srvRecords)
        {
            var authority = record.Port == 443 ? record.Target : $"{record.Target}:{record.Port}";
            if (Uri.TryCreate($"https://{authority}{OutlookAutodiscoverPath}", UriKind.Absolute, out var uri))
                yield return uri;
        }
    }

    /// <summary>
    /// MS-OXDISCO: a plain HTTP GET on the autodiscover host may answer with a redirect to the real HTTPS
    /// endpoint. Nothing is sent over plain HTTP but the request line, and only an HTTPS target is accepted.
    /// </summary>
    private async Task<Uri> GetOutlookHttpRedirectTargetAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _outlookAutodiscoverClient
                .GetAsync($"http://autodiscover.{domain}{OutlookAutodiscoverPath}", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirectStatusCode(response.StatusCode) || response.Headers.Location == null)
                return null;

            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : null;
            return target?.Scheme == Uri.UriSchemeHttps ? target : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Outlook Autodiscover HTTP redirect probe failed for {Domain}", domain);
            return null;
        }
    }

    private async Task<OutlookAutodiscoverResponse> PostOutlookAutodiscoverAsync(
        Uri endpoint,
        string email,
        string localPart,
        string domain,
        OutlookAutodiscoverContext context,
        CancellationToken cancellationToken)
    {
        var useCredentials = false;

        while (true)
        {
            if (endpoint.Scheme != Uri.UriSchemeHttps || !context.TryVisit(endpoint, email, useCredentials))
                return null;

            HttpResponseMessage response;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(BuildOutlookAutodiscoverRequest(email), Encoding.UTF8, "text/xml")
                };

                if (useCredentials)
                {
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.OriginalEmail}:{context.Password}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                }

                response = await _outlookAutodiscoverClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Outlook Autodiscover request to {Endpoint} failed", endpoint);
                return null;
            }

            using (response)
            {
                if (IsRedirectStatusCode(response.StatusCode))
                {
                    if (response.Headers.Location == null || !context.TryUseRedirect())
                        return null;

                    endpoint = Wino.Services.Dav.DavUri.Resolve(endpoint, response.Headers.Location);
                    useCredentials = false;
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    if (useCredentials || !CanSendCredentials(endpoint, response, context) || !context.TryUseCredentials())
                        return null;

                    useCredentials = true;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = ParseOutlookAutodiscoverResponse(content, email, localPart, domain);

                if (parsed?.RedirectUrl == null)
                    return parsed;

                if (!context.TryUseRedirect())
                    return null;

                endpoint = parsed.RedirectUrl;
                useCredentials = false;
            }
        }
    }

    /// <summary>
    /// The password is sent only after the server asks for it with a Basic challenge, only over HTTPS with the
    /// default certificate checks, only to a host inside the mailbox's own domain, and only once per discovery, so a
    /// wrong password counts toward an account lockout at most once. A host reached through SRV or a redirect to
    /// another domain (a hoster, or an autodiscover name someone else controls) never receives it.
    /// </summary>
    private static bool CanSendCredentials(Uri endpoint, HttpResponseMessage response, OutlookAutodiscoverContext context)
    {
        if (string.IsNullOrEmpty(context.Password) || endpoint.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = endpoint.IdnHost;
        var insideDomain = IgnoreCase.Equals(host, context.CredentialDomain)
                           || host.EndsWith("." + context.CredentialDomain, StringComparison.OrdinalIgnoreCase);

        return insideDomain && response.Headers.WwwAuthenticate.Any(header => IgnoreCase.Equals(header.Scheme, "Basic"));
    }

    private static string BuildOutlookAutodiscoverRequest(string email)
    {
        XNamespace ns = OutlookRequestSchema;

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Autodiscover",
                new XElement(ns + "Request",
                    new XElement(ns + "EMailAddress", email),
                    new XElement(ns + "AcceptableResponseSchema", OutlookResponseSchema))));

        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    internal static OutlookAutodiscoverResponse ParseOutlookAutodiscoverResponse(string xmlContent, string email, string localPart, string domain)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return null;

        XDocument document;

        try
        {
            document = XDocument.Parse(xmlContent);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Outlook Autodiscover response is not XML");
            return null;
        }

        if (document.Root?.Name.LocalName != "Autodiscover")
            return null;

        var account = document.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Account");
        if (account == null)
            return null;

        var action = GetElementValue(account, "Action")?.Trim();

        if (IgnoreCase.Equals(action, "redirectAddr"))
        {
            var address = GetElementValue(account, "RedirectAddr")?.Trim();
            return TryGetEmailParts(address, out _, out _) ? new OutlookAutodiscoverResponse(null, address, null) : null;
        }

        if (IgnoreCase.Equals(action, "redirectUrl"))
        {
            var url = GetElementValue(account, "RedirectUrl")?.Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var redirectUri) && redirectUri.Scheme == Uri.UriSchemeHttps
                ? new OutlookAutodiscoverResponse(null, null, redirectUri)
                : null;
        }

        var servers = new List<AutoDiscoveryProviderSetting>();
        Uri davUri = null;

        foreach (var protocol in account.Elements().Where(e => e.Name.LocalName == "Protocol"))
        {
            var type = GetElementValue(protocol, "Type")?.Trim().ToUpperInvariant();

            if (type is "IMAP" or "POP3" or "SMTP")
            {
                var server = ParseOutlookMailServer(protocol, type, email, localPart, domain);
                if (server != null)
                    servers.Add(server);
            }
            else if (type == "DAV" && davUri == null
                     && Uri.TryCreate(GetElementValue(protocol, "Server")?.Trim(), UriKind.Absolute, out var parsedDav)
                     && parsedDav.Scheme == Uri.UriSchemeHttps)
            {
                davUri = parsedDav;
            }
        }

        return servers.Count == 0 && davUri == null
            ? null
            : new OutlookAutodiscoverResponse(new OutlookAutodiscoverResult(servers, davUri), null, null);
    }

    private static AutoDiscoveryProviderSetting ParseOutlookMailServer(XElement protocol, string type, string email, string localPart, string domain)
    {
        var server = GetElementValue(protocol, "Server")?.Trim();
        if (string.IsNullOrWhiteSpace(server) || Uri.CheckHostName(server) == UriHostNameType.Unknown)
            return null;

        if (!int.TryParse(GetElementValue(protocol, "Port"), out var port) || port is <= 0 or > 65535)
            return null;

        var loginName = ResolveTemplate(GetElementValue(protocol, "LoginName")?.Trim(), email, localPart, domain);

        return new AutoDiscoveryProviderSetting
        {
            Protocol = type,
            Address = server,
            Port = port,
            Secure = GetOutlookSocketType(protocol, port),
            Username = string.IsNullOrWhiteSpace(loginName) ? email : loginName
        };
    }

    /// <summary>
    /// Maps Outlook's encryption fields to the socket types the rest of discovery uses. <c>SSL</c> only says the
    /// connection is encrypted and defaults to on (MS-OXDSCLI), so the port decides between
    /// implicit TLS and STARTTLS unless the newer <c>Encryption</c> element says which. With encryption off the
    /// result is empty, which connects with STARTTLS when the server offers it.
    /// </summary>
    internal static string GetOutlookSocketType(XElement protocol, int port)
    {
        var encryption = GetElementValue(protocol, "Encryption")?.Trim();

        if (IgnoreCase.Equals(encryption, "SSL"))
            return "SSL";

        if (IgnoreCase.Equals(encryption, "TLS"))
            return "STARTTLS";

        if (IgnoreCase.Equals(encryption, "None"))
            return string.Empty;

        if (IgnoreCase.Equals(GetElementValue(protocol, "SSL")?.Trim(), "Off"))
            return string.Empty;

        return port is 993 or 995 or 465 ? "SSL" : "STARTTLS";
    }

    internal sealed record OutlookAutodiscoverResult(List<AutoDiscoveryProviderSetting> Servers, Uri DavUri);

    internal sealed record OutlookAutodiscoverResponse(OutlookAutodiscoverResult Result, string RedirectAddress, Uri RedirectUrl);

    private sealed class OutlookAutodiscoverContext(string originalEmail, string password, string credentialDomain)
    {
        private readonly HashSet<string> _visited = new(IgnoreCase);
        private int _redirects;
        private bool _credentialsUsed;

        public string OriginalEmail { get; } = originalEmail;
        public string Password { get; } = password;
        public string CredentialDomain { get; } = credentialDomain;

        public bool TryUseCredentials()
        {
            if (_credentialsUsed)
                return false;

            _credentialsUsed = true;
            return true;
        }

        public bool TryUseRedirect() => ++_redirects <= MaxOutlookAutodiscoverRedirects;

        public bool TryVisit(Uri endpoint, string email, bool withCredentials) => _visited.Add($"{withCredentials}|{email}|{endpoint.AbsoluteUri}");
    }
}
