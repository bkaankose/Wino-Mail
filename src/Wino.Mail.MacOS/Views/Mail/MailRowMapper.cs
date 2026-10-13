using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Core.Domain;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Maps projected mail rows into the ViewModel-free row snapshot drawn by <see cref="WinoMailRowView"/>.
/// Text, dates and aggregation come from the shared <see cref="MailRowPresentation"/>; this adds AppKit colours.
/// </summary>
internal static class MailRowMapper
{
    public static WinoMailRowDensity Density(IPreferencesService preferences) => MailRowPresentation.Density(preferences.MailItemDisplayMode);

    /// <summary>True when a detailed row needs the extra tile line (categories or intelligence tiles).</summary>
    public static bool HasTiles(MailListRow row) => MailRowPresentation.HasTiles(row);

    /// <summary>The row's category and intelligence tiles, as the row shows them (thread heads merge their leaves).</summary>
    public static IReadOnlyList<WinoMailRowTile> Tiles(MailListRow row)
        => HasTiles(row) ? Tiles(MailRowPresentation.Leaves(row)) : Array.Empty<WinoMailRowTile>();

    private static List<WinoMailRowTile> Tiles(MailItemViewModel[] leaves)
    {
        var tiles = new List<WinoMailRowTile>();
        foreach (var category in leaves.SelectMany(static leaf => leaf.Categories).Where(static category => category is not null).DistinctBy(static category => category.Id))
        {
            var background = WinoStyle.FromHexString(category.BackgroundColorHex);
            tiles.Add(new WinoMailRowTile(category.Name ?? string.Empty, null, background, WinoStyle.FromHexString(category.TextColorHex), false));
        }
        foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
            tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));
        return tiles;
    }

    public static WinoMailRowModel Map(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity? densityOverride = null)
    {
        var content = MailRowPresentation.Describe(row, preferences.MailTimeFormatPreference)
            ?? throw new InvalidOperationException("A mail list row must stand for a mail item.");

        return new WinoMailRowModel
        {
            Kind = content.Kind,
            Sender = content.Sender,
            SenderAddress = content.SenderAddress,
            Subject = content.Subject,
            Preview = content.Preview,
            DateText = content.DateText,
            AccessibleDateText = content.AccessibleDateText,
            IsUnread = content.IsUnread,
            IsFlagged = content.IsFlagged,
            IsPinned = content.IsPinned,
            IsDraft = content.IsDraft,
            IsBusy = content.IsBusy,
            HasAttachments = content.HasAttachments,
            Tiles = Tiles(MailRowPresentation.Leaves(row)),
            Density = densityOverride ?? Density(preferences),
            ThreadCount = content.ThreadCount,
            IsThreadExpanded = content.IsThreadExpanded,
            AccountColor = showAccountColor ? WinoStyle.FromHexString(content.AccountColorHex) : null,
            AccountNickname = content.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            ShowPreview = preferences.IsShowPreviewEnabled,
            DraftLabel = Translator.Draft,
            AccessibilityText = content.AccessibilityText
        };
    }

    /// <summary>Shared list date (<see cref="MailRowPresentation.FormatListDate"/>).</summary>
    public static string FormatListDate(DateTime value, TimeFormatPreference timeFormat = TimeFormatPreference.UseLanguageCulture)
        => MailRowPresentation.FormatListDate(value, timeFormat);

    /// <summary>Shared reader date (<see cref="MailRowPresentation.FormatReaderDate"/>).</summary>
    public static string FormatReaderDate(DateTime value, TimeFormatPreference timeFormat = TimeFormatPreference.UseLanguageCulture)
        => MailRowPresentation.FormatReaderDate(value, timeFormat);

    public static string GroupTitle(object key) => MailRowPresentation.GroupTitle(key);
}
