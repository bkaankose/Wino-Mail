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
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.Dav;

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
        var message = status switch
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
