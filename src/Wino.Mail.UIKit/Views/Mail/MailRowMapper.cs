using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Maps projected mail rows into the ViewModel-free row snapshot drawn by <see cref="WinoMailRowCell"/> (UIKit port of
/// the Mac MailRowMapper). Text, dates and aggregation come from the shared <see cref="MailRowPresentation"/>; this adds
/// UIKit colours. One mapper for both idioms: the iPad follows the density preference, the iPhone has no density option
/// and maps every row at <see cref="PhoneDensity"/>.
/// </summary>
public static class MailRowMapper
{
    /// <summary>The iPhone row density (the density preference is not offered on iPhone).</summary>
    public const WinoMailRowDensity PhoneDensity = WinoMailRowDensity.Medium;

    /// <summary>The density preference (iPad).</summary>
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
            tiles.Add(new WinoMailRowTile(category.Name ?? string.Empty, null, WinoStyle.FromHexString(category.BackgroundColorHex), WinoStyle.FromHexString(category.TextColorHex), false));
        foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
            tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));
        return tiles;
    }

    /// <summary>The row snapshot; the row must stand for a mail item.</summary>
    public static WinoMailRowModel Map(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity? densityOverride = null)
        => TryMap(row, preferences, showAccountColor, densityOverride)
           ?? throw new InvalidOperationException("A mail list row must stand for a mail item.");

    /// <summary>The row snapshot, or null when the row does not stand for a mail item.</summary>
    public static WinoMailRowModel? TryMap(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity? densityOverride = null)
    {
        if (MailRowPresentation.Describe(row, preferences.MailTimeFormatPreference) is not { } content) return null;

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

    public static string GroupTitle(object key) => MailRowPresentation.GroupTitle(key);
}
