using Android.Graphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Mail.Controls.Core.MailRow;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// Maps projected mail rows into the ViewModel-free <see cref="WinoMailRowModel"/> drawn by <c>WinoMailRowView</c> (the
/// UIKit and Mac MailRowMapper twin). Text, dates and aggregation come from the shared <see cref="MailRowPresentation"/>;
/// this adds Android colours and the density. Phones have no density option and use Medium rows;
/// tablets (expanded width) follow the density preference, as on Windows and iPad.
/// </summary>
public static class MailRowMapper
{
    public const WinoMailRowDensity PhoneDensity = WinoMailRowDensity.Medium;

    public static WinoMailRowModel? TryMap(MailListRow row, IPreferencesService preferences, bool showAccountColor, WinoMailRowDensity density)
    {
        if (MailRowPresentation.Describe(row, preferences.MailTimeFormatPreference, preferences.AlwaysShowSenderAddress) is not { } content) return null;
        var categories = MailRowPresentation.CategoryIndicators(row, preferences.IsMailListCategoryIndicatorsEnabled);
        var categoryText = MailRowPresentation.CategoryIndicatorsText(categories);
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
            CategoryColors = categories.Select(static category => WinoColors.FromHexString(category.BackgroundColorHex)).ToArray(),
            Density = density,
            ThreadCount = content.ThreadCount,
            IsThreadExpanded = content.IsThreadExpanded,
            AccountColor = showAccountColor ? WinoColors.FromHexString(content.AccountColorHex) : null,
            AccountNickname = content.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            ShowPreview = preferences.IsShowPreviewEnabled,
            DraftLabel = Translator.Draft,
            AccessibilityText = categoryText.Length == 0 ? content.AccessibilityText : $"{content.AccessibilityText}, {categoryText}"
        };
    }

    private static List<WinoMailRowTile> Tiles(MailItemViewModel[] leaves)
    {
        var tiles = new List<WinoMailRowTile>();
        foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
            tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));
        return tiles;
    }

    public static string GroupTitle(object? key) => key is null ? string.Empty : MailRowPresentation.GroupTitle(key);
}
