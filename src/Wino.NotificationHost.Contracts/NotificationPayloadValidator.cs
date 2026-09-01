using System.Xml;

namespace Wino.NotificationHost.Contracts;

public static class NotificationPayloadValidator
{
    private static readonly HashSet<string> AllowedUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-appx",
        "ms-appdata",
        "ms-winsoundevent"
    };

    public static void Validate(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 256 * 1024
        };

        using var stringReader = new StringReader(payload);
        using var reader = XmlReader.Create(stringReader, settings);
        var foundToastElement = false;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (!foundToastElement)
            {
                if (!string.Equals(reader.LocalName, "toast", StringComparison.Ordinal))
                    throw new InvalidDataException("Notification payload root element must be toast.");

                foundToastElement = true;
            }

            if (string.Equals(reader.LocalName, "image", StringComparison.Ordinal) ||
                string.Equals(reader.LocalName, "audio", StringComparison.Ordinal))
            {
                ValidateUri(reader.GetAttribute("src"));
            }

            var activationType = reader.GetAttribute("activationType");
            if (string.Equals(activationType, "protocol", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Protocol activation is not allowed in notification host payloads.");
        }

        if (!foundToastElement)
            throw new InvalidDataException("Notification payload does not contain a toast element.");
    }

    private static void ValidateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !AllowedUriSchemes.Contains(uri.Scheme))
            throw new InvalidDataException($"Notification payload URI scheme is not allowed: {value}");
    }
}
