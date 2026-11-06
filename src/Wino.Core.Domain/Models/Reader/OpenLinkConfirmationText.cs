#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Text for the open link confirmation, shared by every head's dialog.
/// </summary>
public static class OpenLinkConfirmationText
{
    /// <summary>"The link in the message reads …. It opens:", or "This link opens:" without anchor text.</summary>
    public static string GetIntro(string? linkText)
        => string.IsNullOrWhiteSpace(linkText)
            ? Translator.OpenLinkDialog_Destination
            : string.Format(Translator.OpenLinkDialog_LinkText, linkText.Trim());

    /// <summary>The removed parameter count and browser lines, or an empty string when neither applies.</summary>
    public static string GetFooter(int removedParameterCount, string? browserName)
    {
        var parts = new List<string>();

        if (removedParameterCount == 1)
            parts.Add(Translator.OpenLinkDialog_RemovedOneParameter);
        else if (removedParameterCount > 1)
            parts.Add(string.Format(Translator.OpenLinkDialog_RemovedParameters, removedParameterCount));

        if (!string.IsNullOrWhiteSpace(browserName))
            parts.Add(string.Format(Translator.OpenLinkDialog_OpensIn, browserName));

        return string.Join(" ", parts);
    }

    /// <summary>The whole message for heads that show plain text: intro, destination and footer.</summary>
    public static string GetMessage(string? linkText, Uri cleanedUri, int removedParameterCount, string? browserName)
    {
        var footer = GetFooter(removedParameterCount, browserName);
        var message = $"{GetIntro(linkText)}\n{cleanedUri.OriginalString}";

        return string.IsNullOrEmpty(footer) ? message : $"{message}\n\n{footer}";
    }

    /// <summary>
    /// The query parameters of <paramref name="originalUri"/> that <paramref name="cleanedUri"/> no longer has,
    /// joined as they were written, like <c>&amp;utm_source=news&amp;mc_eid=1</c>. Empty when nothing was removed.
    /// </summary>
    public static string GetRemovedQuery(Uri originalUri, Uri cleanedUri)
    {
        if (!originalUri.IsAbsoluteUri || !cleanedUri.IsAbsoluteUri)
            return string.Empty;

        var original = SplitQuery(originalUri.Query);
        var kept = new HashSet<string>(SplitQuery(cleanedUri.Query), StringComparer.Ordinal);
        var removed = original.Where(part => !kept.Contains(part)).ToList();

        if (removed.Count == 0)
            return string.Empty;

        var separator = kept.Count == 0 ? "?" : "&";
        return separator + string.Join("&", removed);
    }

    private static List<string> SplitQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
}
