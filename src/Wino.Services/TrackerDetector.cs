using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Services;

/// <summary>
/// Decides whether a remote reference in a message is an open tracker. Every rule leans towards
/// letting an image through: a false positive hides a real picture, a false negative only leaks an open.
/// </summary>
public static class TrackerDetector
{
    private const int TinyImageMaxPixels = 2;
    private const int OffscreenThresholdPixels = -1000;

    private static readonly Regex TrackingPathRegex = new(Constants.TrackingPathPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HexIdRegex = new("[0-9a-f]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches the delegate <c>HtmlAgilityPackExtensions.ClearRemoteContent</c> takes. Returns null for a reference that is not a tracker.
    /// </summary>
    public static BlockedResourceReason? Inspect(HtmlNode node, string url)
    {
        if (node == null || !TryCreateRemoteUri(url, out var uri))
            return null;

        if (IsKnownTrackerHost(uri.IdnHost))
            return BlockedResourceReason.KnownTracker;

        if (IsImageElement(node))
        {
            var style = ParseStyle(node.GetAttributeValue("style", string.Empty));

            if (IsTiny(node, style))
                return BlockedResourceReason.TinyImage;

            if (IsHidden(node, style))
                return BlockedResourceReason.HiddenImage;
        }

        if (IsTrackingPath(uri))
            return BlockedResourceReason.TrackingPath;

        return null;
    }

    public static bool IsKnownTrackerHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();

        foreach (var trackerHost in Constants.TrackerHosts)
        {
            if (normalized == trackerHost
                || (normalized.Length > trackerHost.Length
                    && normalized.EndsWith(trackerHost, StringComparison.Ordinal)
                    && normalized[normalized.Length - trackerHost.Length - 1] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateRemoteUri(string url, out Uri uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var candidate = url.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            candidate = "https:" + candidate;

        return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsImageElement(HtmlNode node)
        => node.Name.Equals("img", StringComparison.OrdinalIgnoreCase)
           || node.Name.Equals("image", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackingPath(Uri uri)
    {
        if (TrackingPathRegex.IsMatch(uri.AbsolutePath))
            return true;

        var query = uri.Query;
        return query.Contains("open", StringComparison.OrdinalIgnoreCase) && HexIdRegex.IsMatch(query);
    }

    private static bool IsTiny(HtmlNode node, Dictionary<string, string> style)
    {
        var width = GetDimension(node, style, "width");
        var height = GetDimension(node, style, "height");

        if (width.HasValue && height.HasValue)
            return width.Value <= TinyImageMaxPixels && height.Value <= TinyImageMaxPixels;

        // One known dimension alone is not enough for a spacer-sized image; only a 0 or 1 pixel one counts.
        if (width.HasValue || height.HasValue)
            return (width ?? height).Value <= 1;

        return IsDisplayNone(style) && !CanBeRevealedByStylesheet(node);
    }

    private static bool IsHidden(HtmlNode node, Dictionary<string, string> style)
    {
        // Responsive mail hides desktop or mobile images and reveals them with a media query on a class or id.
        if (CanBeRevealedByStylesheet(node))
            return false;

        if (IsDisplayNone(style))
            return true;

        if (style.TryGetValue("visibility", out var visibility) && visibility == "hidden")
            return true;

        if (style.TryGetValue("opacity", out var opacity)
            && double.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacityValue)
            && opacityValue <= 0)
        {
            return true;
        }

        if (style.TryGetValue("position", out var position) && position == "absolute")
        {
            return new[] { "left", "top" }
                .Select(side => style.TryGetValue(side, out var value) ? ParsePixels(value) : null)
                .Any(value => value.HasValue && value.Value <= OffscreenThresholdPixels);
        }

        return false;
    }

    private static bool CanBeRevealedByStylesheet(HtmlNode node)
        => !string.IsNullOrWhiteSpace(node.GetAttributeValue("class", null))
           || !string.IsNullOrWhiteSpace(node.GetAttributeValue("id", null));

    private static bool IsDisplayNone(Dictionary<string, string> style)
        => style.TryGetValue("display", out var display) && display == "none";

    private static double? GetDimension(HtmlNode node, Dictionary<string, string> style, string name)
    {
        if (style.TryGetValue(name, out var styleValue))
        {
            var fromStyle = ParsePixels(styleValue);
            if (fromStyle.HasValue)
                return fromStyle;
        }

        var attribute = node.GetAttributeValue(name, null);
        return string.IsNullOrWhiteSpace(attribute) ? null : ParsePixels(attribute);
    }

    private static double? ParsePixels(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed.EndsWith("px", StringComparison.Ordinal))
            trimmed = trimmed[..^2].TrimEnd();
        else if (trimmed.Any(c => char.IsLetter(c) || c == '%'))
            return null;

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) ? pixels : null;
    }

    private static Dictionary<string, string> ParseStyle(string style)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(style))
            return declarations;

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..]
                .Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim()
                .ToLowerInvariant();

            declarations[name] = value;
        }

        return declarations;
    }
}
