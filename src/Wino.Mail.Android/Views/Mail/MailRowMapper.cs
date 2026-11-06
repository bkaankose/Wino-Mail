using Android.Graphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// Maps projected mail rows into the ViewModel-free <see cref="WinoMailRowModel"/> drawn by <c>WinoMailRowView</c> (the
/// UIKit and Mac MailRowMapper twin). Text, dates and aggregation come from the shared <see cref="MailRowPresentation"/>;
/// this adds Android colours, the sender picture and the density. Phones have no density option and use Medium rows;
/// tablets (expanded width) follow the density preference, as on Windows and iPad.
/// </summary>
public static class MailRowMapper
{
    public const WinoMailRowDensity PhoneDensity = WinoMailRowDensity.Medium;

    public static WinoMailRowModel? TryMap(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity density, Bitmap? picture)
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
            Tiles = MailRowPresentation.HasTiles(row) ? Tiles(MailRowPresentation.Leaves(row)) : Array.Empty<WinoMailRowTile>(),
            Density = density,
            ThreadCount = content.ThreadCount,
            IsThreadExpanded = content.IsThreadExpanded,
            AccountColor = showAccountColor ? WinoColors.FromHexString(content.AccountColorHex) : null,
            AccountNickname = content.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            Picture = preferences.IsShowSenderPicturesEnabled ? picture : null,
            ShowPreview = preferences.IsShowPreviewEnabled,
            DraftLabel = Translator.Draft,
            AccessibilityText = content.AccessibilityText
        };
    }

    private static List<WinoMailRowTile> Tiles(MailItemViewModel[] leaves)
    {
        var tiles = new List<WinoMailRowTile>();
        foreach (var category in leaves.SelectMany(static leaf => leaf.Categories).Where(static category => category is not null).DistinctBy(static category => category.Id))
            tiles.Add(new WinoMailRowTile(category.Name ?? string.Empty, null, WinoColors.FromHexString(category.BackgroundColorHex), WinoColors.FromHexString(category.TextColorHex), false));
        foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
            tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));
        return tiles;
    }

    public static string GroupTitle(object? key) => key is null ? string.Empty : MailRowPresentation.GroupTitle(key);
}
