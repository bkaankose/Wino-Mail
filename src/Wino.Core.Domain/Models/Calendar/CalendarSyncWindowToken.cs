using System;
using System.Globalization;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Persists the provider synchronization token together with the end of the
/// time window it was created for. Provider delta tokens (Graph calendar view,
/// Google sync tokens) and CalDAV report windows are all bound to that window.
/// Once the window end gets close, the calendar must be re-anchored with a fresh
/// full download so later occurrences and new events keep arriving.
/// </summary>
public static class CalendarSyncWindowToken
{
    private const string WindowPrefix = "win=";
    private const char Separator = '|';
    private const string DateFormat = "yyyyMMdd";

    /// <summary>
    /// A calendar is re-anchored when fewer than this many months of window remain.
    /// </summary>
    public static readonly TimeSpan ReanchorThreshold = TimeSpan.FromDays(183);

    public static string Encode(string providerToken, DateTimeOffset windowEndUtc)
        => $"{WindowPrefix}{windowEndUtc.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)}{Separator}{providerToken ?? string.Empty}";

    /// <summary>
    /// Splits a stored token into the provider token and the window end it belongs to.
    /// Tokens stored before window anchoring existed return a null window end.
    /// </summary>
    public static (string ProviderToken, DateTimeOffset? WindowEndUtc) Decode(string storedToken)
    {
        if (string.IsNullOrWhiteSpace(storedToken))
            return (string.Empty, null);

        if (!storedToken.StartsWith(WindowPrefix, StringComparison.Ordinal))
            return (storedToken, null);

        var separatorIndex = storedToken.IndexOf(Separator, WindowPrefix.Length);
        if (separatorIndex < 0)
            return (storedToken, null);

        var dateText = storedToken[WindowPrefix.Length..separatorIndex];
        if (!DateTime.TryParseExact(dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var windowEnd))
            return (storedToken, null);

        return (storedToken[(separatorIndex + 1)..], new DateTimeOffset(DateTime.SpecifyKind(windowEnd, DateTimeKind.Utc)));
    }

    /// <summary>
    /// True when the stored token has no window information (legacy) or the
    /// remaining window is shorter than <see cref="ReanchorThreshold"/>.
    /// </summary>
    public static bool RequiresReanchor(DateTimeOffset? windowEndUtc, DateTimeOffset nowUtc)
        => windowEndUtc == null || windowEndUtc.Value - nowUtc < ReanchorThreshold;
}
