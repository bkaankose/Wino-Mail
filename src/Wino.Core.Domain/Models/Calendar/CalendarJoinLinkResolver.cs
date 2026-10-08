using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Domain.Models.Calendar;

public static partial class CalendarJoinLinkResolver
{
    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"'];

    public static string ResolveDirectJoinLink(string structuredLink, string content)
    {
        if (TryCreateStructuredUri(structuredLink, out var structuredUri))
            return UnwrapSafeLink(structuredUri).AbsoluteUri;

        return FindVideoConferenceLink(content)?.Url.AbsoluteUri;
    }

    public static VideoConferenceLink FindVideoConferenceLink(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var decodedContent = WebUtility.HtmlDecode(content);
        var candidates = new List<(int Index, string Value)>();

        foreach (Match match in HrefRegex().Matches(decodedContent))
        {
            var value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;

            candidates.Add((match.Index, value));
        }

        foreach (Match match in UrlCandidateRegex().Matches(decodedContent))
        {
            candidates.Add((match.Index, match.Value));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Index))
        {
            var normalized = NormalizeCandidate(candidate.Value);
            if (normalized == null || !seen.Add(normalized) ||
                !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var matchingUri = UnwrapSafeLink(uri);
            if (VideoConferenceService.TryGetService(matchingUri, out var service))
                return new VideoConferenceLink(service, matchingUri);
        }

        return null;
    }

    public static bool TryGetEffectiveJoinUri(CalendarItem calendarItem, out Uri uri)
    {
        uri = null;

        if (calendarItem == null)
            return false;

        if (Uri.TryCreate(calendarItem.DirectJoinLink, UriKind.Absolute, out var directUri) &&
            (IsHttpUri(directUri) || VideoConferenceService.TryGetService(directUri, out _)))
        {
            uri = UnwrapSafeLink(directUri);
            return true;
        }

        if (Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out var htmlUri) && IsHttpUri(htmlUri))
        {
            uri = htmlUri;
            return true;
        }

        return false;
    }

    private static bool TryCreateStructuredUri(string value, out Uri uri)
    {
        uri = null;
        return Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
               IsHttpUri(candidate) &&
               (uri = candidate) != null;
    }

    private static string NormalizeCandidate(string value)
    {
        var candidate = WebUtility.HtmlDecode(value)?.Trim().TrimEnd(TrailingPunctuation);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = $"https://{candidate}";

        return candidate;
    }

    private static Uri UnwrapSafeLink(Uri uri)
        => VideoConferenceService.TryUnwrapOutlookSafeLink(uri, out var unwrappedUri) ? unwrappedUri : uri;

    private static bool IsHttpUri(Uri uri)
        => uri.IsAbsoluteUri &&
           (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("href\\s*=\\s*(?:\\\"(?<double>[^\\\"]+)\\\"|'(?<single>[^']+)'|(?<bare>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("(?:(?:https?|zoommtg|ts3server)://|(?:[a-z0-9-]+\\.)+[a-z]{2,})(?:[^\\s<>\\\"']*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCandidateRegex();
}
