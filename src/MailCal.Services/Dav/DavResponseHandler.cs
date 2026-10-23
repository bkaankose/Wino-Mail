using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.CardDav;

namespace MailCal.Services.Dav;

/// <summary>
/// Maps WebDAV HTTP failures without assuming every 403 is an authentication failure.
/// See <see href="https://www.rfc-editor.org/rfc/rfc4918#section-11"/> for DAV status use.
/// </summary>
public sealed class DavResponseHandler : IDavResponseHandler
{
    public async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
            return;

        var errors = await ReadErrorsAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var operation = response.RequestMessage?.Method.Method;
        var status = response.StatusCode;
        var message = GetPreconditionMessage(errors, status) ?? status switch
        {
            HttpStatusCode.Unauthorized => Translator.DavError_AuthenticationRequired,
            HttpStatusCode.Forbidden when string.Equals(operation, "MKCOL", StringComparison.OrdinalIgnoreCase)
                => Translator.DavError_CollectionCreationDenied,
            HttpStatusCode.Forbidden => Translator.DavError_PermissionDenied,
            HttpStatusCode.NotFound => Translator.DavError_NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => Translator.DavError_Conflict,
            (HttpStatusCode)423 => Translator.DavError_ReadOnly,
            (HttpStatusCode)429 => Translator.DavError_RateLimited,
            >= HttpStatusCode.InternalServerError => Translator.DavError_ServerUnavailable,
            _ => Translator.DavError_InvalidResponse
        };

        throw new DavRequestException(
            (int)status,
            message,
            errors,
            response.Headers.RetryAfter?.Delta);
    }

    /// <summary>
    /// Storage and size refusals carry their own meaning whatever the status: 507 Insufficient
    /// Storage (RFC 4918 section 11.5), the quota preconditions of RFC 4331 section 6, and the
    /// CalDAV <c>max-resource-size</c> and <c>max-attendees-per-instance</c> preconditions
    /// (RFC 4791 section 5.3.2.1).
    /// </summary>
    internal static string GetPreconditionMessage(IReadOnlyList<string> errors, HttpStatusCode status)
    {
        if (HasError(errors, "max-resource-size"))
            return Translator.DavError_ResourceTooLarge;

        if (HasError(errors, "max-attendees-per-instance"))
            return Translator.DavError_TooManyAttendees;

        if (status == HttpStatusCode.InsufficientStorage ||
            HasError(errors, "quota-not-exceeded") ||
            HasError(errors, "sufficient-disk-space"))
        {
            return Translator.DavError_QuotaExceeded;
        }

        return null;
    }

    private static bool HasError(IReadOnlyList<string> errors, string localName)
        => errors.Any(value => value.EndsWith($"}}{localName}", StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<string>> ReadErrorsAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content is null)
            return [];

        var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return [];

        try
        {
            using var textReader = new StringReader(body);
            using var xmlReader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var xml = XDocument.Load(xmlReader);

            return xml.Descendants()
                .Where(element => element.Parent?.Name.LocalName == "error")
                .Select(element => $"{{{element.Name.NamespaceName}}}{element.Name.LocalName}")
                .ToArray();
        }
        catch (XmlException)
        {
            return [];
        }
    }
}
