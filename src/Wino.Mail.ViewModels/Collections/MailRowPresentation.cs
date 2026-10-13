#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Collections;

/// <summary>
/// The text and state a native mail row shows, before any platform colour or image is attached.
/// Thread heads aggregate their leaves (unread, flagged, attachments).
/// </summary>
public sealed record MailRowContent(
    WinoMailRowKind Kind,
    string Sender,
    string SenderAddress,
    string Subject,
    string Preview,
    string DateText,
    string AccessibleDateText,
    bool IsUnread,
    bool IsFlagged,
    bool IsPinned,
    bool IsDraft,
    bool IsBusy,
    bool HasAttachments,
    int ThreadCount,
    bool IsThreadExpanded,
    string? AccountColorHex,
    string? AccountNickname,
    string AccessibilityText);

/// <summary>
/// Shared presentation rules for native mail lists and readers: thread aggregation, sender and subject
/// fallbacks, accessibility text, list and reader dates, and group titles. Native heads map the result
/// to their row view model and add colours and pictures.
/// </summary>
public static class MailRowPresentation
{
    private static CultureInfo Culture => CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;

    public static WinoMailRowDensity Density(MailListDisplayMode mode) => mode switch
    {
        MailListDisplayMode.Compact => WinoMailRowDensity.Compact,
        MailListDisplayMode.Spacious => WinoMailRowDensity.Spacious,
        _ => WinoMailRowDensity.Medium
    };

    public static WinoMailRowKind Kind(MailListRowKind kind) => kind switch
    {
        MailListRowKind.ThreadHead => WinoMailRowKind.ThreadHead,
        MailListRowKind.ThreadChild => WinoMailRowKind.ThreadChild,
        _ => WinoMailRowKind.Single
    };

    /// <summary>The mail items a row stands for: every leaf of a thread head, otherwise the row's own item.</summary>
    public static MailItemViewModel[] Leaves(MailListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsThreadHead) return row.LeafItems.OfType<MailItemViewModel>().ToArray();
        return row.SourceItem is MailItemViewModel item ? [item] : [];
    }

    /// <summary>True when a detailed row needs the extra tile line (categories or intelligence tiles).</summary>
    public static bool HasTiles(MailListRow row)
        => Leaves(row).Any(static leaf => leaf.HasCategories || leaf.HasRowIntelligenceTiles);

    /// <summary>The row's text and aggregated state; null when the row does not stand for a mail item.</summary>
    public static MailRowContent? Describe(MailListRow row, TimeFormatPreference timeFormat = TimeFormatPreference.UseLanguageCulture)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.SourceItem is not MailItemViewModel item) return null;

        var leaves = Leaves(row);
        bool unread = leaves.Any(static leaf => !leaf.IsRead);
        bool flagged = leaves.Any(static leaf => leaf.IsFlagged);
        bool hasAttachments = leaves.Any(static leaf => leaf.HasAttachments);
        int threadCount = row.IsThreadHead ? row.LeafItems.Count : 0;

        var sender = string.IsNullOrWhiteSpace(item.FromName) ? item.FromAddress ?? string.Empty : item.FromName;
        var accessibleSubject = string.IsNullOrWhiteSpace(item.Subject) ? Translator.MailItemNoSubject : item.Subject;
        var accessibleDate = FormatAccessibleDate(item.CreationDate);

        var accessible = new List<string> { sender, accessibleSubject, accessibleDate };
        if (unread) accessible.Add(Translator.Accessibility_MailItemReadState_Unread);
        if (flagged) accessible.Add(Translator.FilteringOption_Flagged);
        if (hasAttachments) accessible.Add(Translator.SearchBar_HasAttachments);
        if (item.IsDraft) accessible.Add(Translator.Draft);
        if (threadCount > 1) accessible.Add(threadCount.ToString(Culture));

        return new MailRowContent(
            Kind(row.Kind),
            item.IsDraft && string.IsNullOrWhiteSpace(item.FromName) ? Translator.Draft : sender,
            item.FromAddress ?? string.Empty,
            SubjectForListing(item.Subject),
            item.PreviewText ?? string.Empty,
            FormatListDate(item.CreationDate, timeFormat),
            accessibleDate,
            unread,
            flagged,
            item.IsPinned,
            item.IsDraft,
            item.IsBusy,
            hasAttachments,
            threadCount,
            row.IsExpanded,
            item.AccountColorHex,
            item.AccountNickname,
            string.Join(", ", accessible.Where(static part => !string.IsNullOrWhiteSpace(part))));
    }

    /// <summary>The list subject, "(No subject)" when empty (Windows GetMailItemSubjectForListing).</summary>
    public static string SubjectForListing(string? subject)
        => string.IsNullOrWhiteSpace(subject) ? $"({Translator.MailItemNoSubject})" : subject;

    /// <summary>
    /// Time today (in the user's time format), weekday within the last week, short month and day this year,
    /// otherwise the short date. Empty for an unset date.
    /// </summary>
    public static string FormatListDate(DateTime value, TimeFormatPreference timeFormat = TimeFormatPreference.UseLanguageCulture, DateTime? today = null)
    {
        if (value == default) return string.Empty;

        var culture = Culture;
        var local = value.ToLocalTime();
        var currentDay = (today ?? DateTime.Today).Date;

        if (local.Date == currentDay) return FormatTime(local, timeFormat, culture);
        if (local.Date > currentDay.AddDays(-7)) return local.ToString("ddd", culture);
        if (local.Year == currentDay.Year) return local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), culture);
        return local.ToString("d", culture);
    }

    public static string FormatAccessibleDate(DateTime value)
        => value == default ? string.Empty : value.ToLocalTime().ToString("f", Culture);

    /// <summary>Reader header date: "Today 14:05", "Yesterday 09:30" or the long date plus time.</summary>
    public static string FormatReaderDate(DateTime value, TimeFormatPreference timeFormat = TimeFormatPreference.UseLanguageCulture)
    {
        if (value == default) return string.Empty;

        var culture = Culture;
        var local = value.ToLocalTime();
        var day = local.Date == DateTime.Today ? Translator.Today
            : local.Date == DateTime.Today.AddDays(-1) ? Translator.Yesterday
            : local.ToString("D", culture);

        return $"{day} {FormatTime(local, timeFormat, culture)}";
    }

    /// <summary>Title of a projected list group: pinned section, date group or the key's text.</summary>
    public static string GroupTitle(object? key)
    {
        if (key is MailListProjectionGroupKey projected)
        {
            if (projected.IsPinned) return Translator.FolderCustomization_SectionPinned;
            key = projected.Value;
        }

        return key switch
        {
            DateTime date => new DateGroupHeader(date).DisplayName,
            string text => text,
            null => string.Empty,
            _ => key.ToString() ?? string.Empty
        };
    }

    private static string FormatTime(DateTime local, TimeFormatPreference timeFormat, CultureInfo culture)
        => DateTimeDisplayFormatter.FormatTime(local, DateTimeDisplayFormatter.GetTimeDisplayType(timeFormat, culture), culture);
}
