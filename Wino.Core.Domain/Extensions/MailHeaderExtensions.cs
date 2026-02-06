using System;
using System.Linq;

namespace Wino.Core.Domain.Extensions;

public static class MailHeaderExtensions
{
    /// <summary>
    /// Strips angle brackets from a Message-ID or In-Reply-To value.
    /// RFC 5322 Message-IDs are formatted as &lt;id@domain&gt;, but MimeKit
    /// properties store them without brackets. This normalizes raw header
    /// values to match MimeKit's convention.
    /// </summary>
    public static string StripAngleBrackets(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        value = value.Trim();

        if (value.StartsWith("<") && value.EndsWith(">"))
            return value.Substring(1, value.Length - 2);

        return value;
    }

    /// <summary>
    /// Normalizes a raw RFC References header value into semicolon-separated Message-IDs
    /// without angle brackets. Raw References headers contain space-separated bracketed IDs
    /// like "&lt;id1@domain&gt; &lt;id2@domain&gt;". This converts them to "id1@domain;id2@domain".
    /// </summary>
    public static string NormalizeReferences(string rawReferences)
    {
        if (string.IsNullOrEmpty(rawReferences)) return rawReferences;

        var ids = rawReferences
            .Split(new[] { ' ', '\t', '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(StripAngleBrackets)
            .Where(id => !string.IsNullOrEmpty(id));

        return string.Join(";", ids);
    }
}
