using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.MailRow;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Collections;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>Maps a projected mail row into the snapshot <see cref="WinoMailRow"/> draws (the macOS mapper).</summary>
internal static class MailRowMapper
{
    public static WinoMailRowDensity Density(IPreferencesService preferences) => MailRowPresentation.Density(preferences.MailItemDisplayMode);

    /// <summary>Null when the row does not stand for a mail item. An unknown sender picture starts a lookup that raises <see cref="LinuxSenderPictureProvider.PictureChanged"/>.</summary>
    public static WinoMailRowModel? Map(MailListRow row, IPreferencesService preferences, bool showAccountColor, LinuxSenderPictureProvider? pictures, WinoMailRowDensity? densityOverride = null)
    {
        if (MailRowPresentation.Describe(row, preferences.MailTimeFormatPreference, preferences.AlwaysShowSenderAddress) is not { } content) return null;
        var categories = MailRowPresentation.CategoryIndicators(row, preferences.IsMailListCategoryIndicatorsEnabled);
        return new WinoMailRowModel
        {
            Kind = content.Kind,
            Density = densityOverride ?? Density(preferences),
            Sender = content.Sender,
            SenderAddress = content.SenderAddress,
            Subject = content.Subject,
            Preview = content.Preview,
            DateText = content.DateText,
            IsUnread = content.IsUnread,
            IsFlagged = content.IsFlagged,
            IsPinned = content.IsPinned,
            IsDraft = content.IsDraft,
            IsBusy = content.IsBusy,
            HasAttachments = content.HasAttachments,
            ThreadCount = content.ThreadCount,
            IsThreadExpanded = content.IsThreadExpanded,
            AccountColorHex = showAccountColor ? content.AccountColorHex : null,
            AccountNickname = content.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            Picture = preferences.IsShowSenderPicturesEnabled ? pictures?.Get(content.SenderAddress) : null,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            ShowPreview = preferences.IsShowPreviewEnabled,
            AccessibilityText = content.AccessibilityText,
            Tiles = Tiles(row),
            CategoryDotColors = [.. categories.Select(static category => category.BackgroundColorHex)],
            CategoryNames = MailRowPresentation.CategoryIndicatorsText(categories)
        };
    }

    /// <summary>The intelligence tiles of the row's leaves (distinct by text and glyph).</summary>
    public static WinoMailRowTile[] Tiles(MailListRow row)
    {
        if (!MailRowPresentation.HasTiles(row)) return [];
        var leaves = MailRowPresentation.Leaves(row);
        var tiles = new List<WinoMailRowTile>();
        var intelligence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (!leaf.HasRowIntelligenceTiles) continue;
            foreach (var tile in leaf.RowIntelligenceTiles)
            {
                if (!intelligence.Add(tile.Text + tile.Glyph)) continue;
                tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning,
                    string.IsNullOrEmpty(tile.AccessibleText) ? null : tile.AccessibleText));
            }
        }
        return [.. tiles];
    }
}
