using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Wino.Mapi.Transport;

/// <summary>
/// What the transport needs from Autodiscover to open a mailbox, plus the other mailboxes the
/// response names: the in-place archive (an AlternativeMailbox of type Archive) and the public
/// folder mailbox (PublicFolderInformation), each reachable through its own Autodiscover.
/// </summary>
public sealed record MapiEndpointInfo(Uri MailStoreUrl, Uri? AddressBookUrl, string LegacyDn, string? DisplayName,
    string? ArchiveSmtpAddress = null, string? ArchiveLegacyDn = null, string? PublicFolderSmtpAddress = null);

/// <summary>
/// Classic Autodiscover (POX, MS-OXDSCLI). Two things are wanted from it: the routable MAPI/HTTP
/// endpoint (the MailStore URL, which carries the MailboxId the front end routes on), and the
/// mailbox's legacyExchangeDN, which Connect and Logon both take. Both come back in one response.
/// </summary>
public static class MapiAutodiscover
{
    public static async Task<MapiEndpointInfo> DiscoverAsync(Uri autodiscoverUrl, string smtpAddress, MapiCredential credential, CancellationToken cancellationToken = default)
    {
        var request =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006">
              <Request>
                <EMailAddress>{smtpAddress}</EMailAddress>
                <AcceptableResponseSchema>http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a</AcceptableResponseSchema>
              </Request>
            </Autodiscover>
            """;

        using var handler = new HttpClientHandler();
        if (credential is MapiCredential.Integrated integrated)
        {
            handler.Credentials = new CredentialCache
            {
                { autodiscoverUrl, integrated.Scheme, integrated.Credentials ?? CredentialCache.DefaultNetworkCredentials },
            };
            handler.PreAuthenticate = true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        if (credential is MapiCredential.Bearer bearer)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token);
        }

        // Outlook sends this header; some front ends key MAPI/HTTP advertisement on it.
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-MapiHttpCapability", "1");

        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync(autodiscoverUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new MapiTransportException("Autodiscover", response.StatusCode, null, $"Autodiscover returned HTTP {(int)response.StatusCode}.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new MapiFormatException("Autodiscover response was not XML: " + ex.Message);
        }

        return Parse(document);
    }

    /// <summary>Namespace-agnostic: the response schema namespace varies by version.</summary>
    public static MapiEndpointInfo Parse(XDocument document)
    {
        static string? Local(XElement parent, string name)
            => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

        var user = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "User");
        var legacyDn = user is null ? null : Local(user, "LegacyDN");
        if (string.IsNullOrWhiteSpace(legacyDn))
        {
            var error = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Error");
            var message = error is null ? "no <User><LegacyDN> in the response" : Local(error, "Message") ?? "an <Error> without a message";
            throw new MapiFormatException("Autodiscover did not identify the mailbox: " + message + ".");
        }

        var mapiProtocol = document.Descendants()
            .Where(e => e.Name.LocalName == "Protocol")
            .FirstOrDefault(p => (Local(p, "Type") ?? p.Attribute("Type")?.Value ?? "")
                .Contains("mapiHttp", StringComparison.OrdinalIgnoreCase));

        if (mapiProtocol is null)
        {
            throw new MapiNotAdvertisedException("Autodiscover advertised no mapiHttp protocol for this mailbox. The server may predate Exchange 2013 SP1, or MAPI/HTTP may be disabled for the organisation or the user.");
        }

        static Uri? FirstUrl(XElement protocol, string container)
        {
            var element = protocol.Elements().FirstOrDefault(e => e.Name.LocalName == container);
            if (element is null) return null;

            // Prefer the external URL: the client may be anywhere. Fall back to internal.
            var external = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ExternalUrl")?.Value;
            var internalUrl = element.Elements().FirstOrDefault(e => e.Name.LocalName == "InternalUrl")?.Value;
            var chosen = !string.IsNullOrWhiteSpace(external) ? external : internalUrl;
            return Uri.TryCreate(chosen, UriKind.Absolute, out var uri) ? uri : null;
        }

        var mailStore = FirstUrl(mapiProtocol, "MailStore")
            ?? throw new MapiFormatException("The mapiHttp protocol carried no MailStore URL.");

        var archive = document.Descendants()
            .Where(e => e.Name.LocalName == "AlternativeMailbox")
            .FirstOrDefault(e => string.Equals(Local(e, "Type"), "Archive", StringComparison.OrdinalIgnoreCase));
        var publicFolders = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "PublicFolderInformation");

        return new MapiEndpointInfo(mailStore, FirstUrl(mapiProtocol, "AddressBook"), legacyDn, user is null ? null : Local(user, "DisplayName"),
            ArchiveSmtpAddress: archive is null ? null : Local(archive, "SmtpAddress"),
            ArchiveLegacyDn: archive is null ? null : Local(archive, "LegacyDN"),
            PublicFolderSmtpAddress: publicFolders is null ? null : Local(publicFolders, "SmtpAddress"));
    }
}
