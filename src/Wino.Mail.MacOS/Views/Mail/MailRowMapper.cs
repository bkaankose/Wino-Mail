using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Maps projected mail rows into the ViewModel-free row snapshot drawn by <see cref="WinoMailRowView"/>.</summary>
internal static class MailRowMapper
{
    private static CultureInfo Culture => CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;

    public static WinoMailRowDensity Density(IPreferencesService preferences) => preferences.MailItemDisplayMode switch
    {
        MailListDisplayMode.Compact => WinoMailRowDensity.Compact,
        MailListDisplayMode.Spacious => WinoMailRowDensity.Spacious,
        _ => WinoMailRowDensity.Medium
    };

    /// <summary>True when a detailed row needs the extra tile line (categories or intelligence tiles).</summary>
    public static bool HasTiles(MailListRow row)
    {
        foreach (var leaf in row.IsThreadHead ? row.LeafItems : [row.SourceItem])
            if (leaf is MailItemViewModel item && (item.HasCategories || item.HasRowIntelligenceTiles)) return true;
        return false;
    }

    public static WinoMailRowModel Map(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity? densityOverride = null)
    {
        var item = (MailItemViewModel)row.SourceItem;
        var kind = row.Kind switch
        {
            MailListRowKind.ThreadHead => WinoMailRowKind.ThreadHead,
            MailListRowKind.ThreadChild => WinoMailRowKind.ThreadChild,
            _ => WinoMailRowKind.Single
        };

        var leaves = row.IsThreadHead ? row.LeafItems.OfType<MailItemViewModel>().ToArray() : [item];
        bool unread = leaves.Any(static leaf => !leaf.IsRead);
        bool flagged = leaves.Any(static leaf => leaf.IsFlagged);
        int attachments = leaves.Count(static leaf => leaf.HasAttachments);
        var tiles = new List<WinoMailRowTile>();
        foreach (var category in leaves.SelectMany(static leaf => leaf.Categories).Where(static category => category is not null).DistinctBy(static category => category.Id))
        {
            var background = WinoStyle.FromHexString(category.BackgroundColorHex);
            tiles.Add(new WinoMailRowTile(category.Name ?? string.Empty, null, background, WinoStyle.FromHexString(category.TextColorHex), false));
        }
        foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
            tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));

        var sender = string.IsNullOrWhiteSpace(item.FromName) ? item.FromAddress ?? string.Empty : item.FromName;
        var subject = string.IsNullOrWhiteSpace(item.Subject) ? Translator.MailItemNoSubject : item.Subject;
        var date = FormatListDate(item.CreationDate);
        var accessible = new List<string> { sender, subject, FormatAccessibleDate(item.CreationDate) };
        if (unread) accessible.Add(Translator.Accessibility_MailItemReadState_Unread);
        if (flagged) accessible.Add(Translator.FilteringOption_Flagged);
        if (attachments > 0) accessible.Add(Translator.SearchBar_HasAttachments);
        if (item.IsDraft) accessible.Add(Translator.Draft);
        if (row.IsThreadHead && row.LeafItems.Count > 1) accessible.Add(row.LeafItems.Count.ToString(Culture));

        return new WinoMailRowModel
        {
            Kind = kind,
            Sender = item.IsDraft && string.IsNullOrWhiteSpace(item.FromName) ? Translator.Draft : sender,
            SenderAddress = item.FromAddress ?? string.Empty,
            Subject = string.IsNullOrWhiteSpace(item.Subject) ? $"({Translator.MailItemNoSubject})" : item.Subject,
            Preview = item.PreviewText ?? string.Empty,
            DateText = date,
            AccessibleDateText = accessible[2],
            IsUnread = unread,
            IsFlagged = flagged,
            IsPinned = item.IsPinned,
            IsDraft = item.IsDraft,
            IsBusy = item.IsBusy,
            HasAttachments = attachments > 0,
            Tiles = tiles,
            Density = densityOverride ?? Density(preferences),
            ThreadCount = row.IsThreadHead ? row.LeafItems.Count : 0,
            IsThreadExpanded = row.IsExpanded,
            AccountColor = showAccountColor ? WinoStyle.FromHexString(item.AccountColorHex) : null,
            AccountNickname = item.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            ShowPreview = preferences.IsShowPreviewEnabled,
            DraftLabel = Translator.Draft,
            AccessibilityText = string.Join(", ", accessible.Where(static part => !string.IsNullOrWhiteSpace(part)))
        };
    }

    /// <summary>Time today, weekday within the last week, short month and day this year, otherwise the short date.</summary>
    public static string FormatListDate(DateTime value)
    {
        if (value == default) return string.Empty;
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("t", Culture);
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd", Culture);
        if (local.Year == today.Year) return local.ToString(Culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), Culture);
        return local.ToString("d", Culture);
    }

    public static string FormatAccessibleDate(DateTime value)
    {
        if (value == default) return string.Empty;
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return local.ToString("f", Culture);
    }

    /// <summary>Reader header date: "Today at 14:05" style long date plus time.</summary>
    public static string FormatReaderDate(DateTime value)
    {
        if (value == default) return string.Empty;
        var local = value.ToLocalTime();
        var day = local.Date == DateTime.Today ? Translator.Today
            : local.Date == DateTime.Today.AddDays(-1) ? Translator.Yesterday
            : local.ToString("D", Culture);
        return $"{day} {local.ToString("t", Culture)}";
    }

    public static string GroupTitle(object key)
    {
        if (key is MailListProjectionGroupKey projected)
        {
            if (projected.IsPinned) return Translator.FolderCustomization_SectionPinned;
            key = projected.Value!;
        }
        return key switch
        {
            DateTime date => new DateGroupHeader(date).DisplayName,
            string text => text,
            null => string.Empty,
            _ => key.ToString() ?? string.Empty
        };
    }
}
