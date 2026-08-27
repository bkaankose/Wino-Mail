using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.Dav;

public sealed class DavMultistatusReader : IDavMultistatusReader
{
    public async Task<DavMultistatus> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var result = new DavMultistatus();
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "response" && reader.NamespaceURI == "DAV:")
            {
                using var subtree = reader.ReadSubtree();
                var responseElement = await XElement.LoadAsync(subtree, LoadOptions.PreserveWhitespace, cancellationToken).ConfigureAwait(false);
                result.Responses.Add(ParseResponse(responseElement));
            }
            else if (reader.LocalName == "sync-token" && reader.NamespaceURI == "DAV:")
            {
                result.SyncToken = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            }
        }
        return result;
    }

    private static DavResponseItem ParseResponse(XElement element)
    {
        XNamespace dav = "DAV:";
        var response = new DavResponseItem
        {
            Href = element.Element(dav + "href")?.Value,
            StatusCode = ParseStatus(element.Element(dav + "status")?.Value)
        };

        foreach (var propstatElement in element.Elements(dav + "propstat"))
        {
            var status = new DavPropertyStatus
            {
                StatusCode = ParseStatus(propstatElement.Element(dav + "status")?.Value)
            };
            var propertyContainer = propstatElement.Element(dav + "prop");
            if (propertyContainer is not null)
            {
                foreach (var property in propertyContainer.Elements())
                {
                    status.Properties.Add(new DavProperty
                    {
                        Namespace = property.Name.NamespaceName,
                        Name = property.Name.LocalName,
                        Value = property.Value,
                        Xml = property.ToString(SaveOptions.DisableFormatting)
                    });
                }
            }
            status.ErrorNames.AddRange(ReadErrorNames(propstatElement.Element(dav + "error")));
            response.PropertyStatuses.Add(status);
        }

        response.ErrorNames.AddRange(ReadErrorNames(element.Element(dav + "error")));
        return response;
    }

    private static string[] ReadErrorNames(XElement error)
        => error?.DescendantsAndSelf().Skip(1).Select(item => $"{{{item.Name.NamespaceName}}}{item.Name.LocalName}").Distinct().ToArray() ?? [];

    private static int? ParseStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : null;
    }
}
