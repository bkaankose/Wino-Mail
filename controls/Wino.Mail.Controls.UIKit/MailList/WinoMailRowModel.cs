using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>
/// One tile on a mail row (the Windows RowTiles): a category (coloured, text) or an intelligence tile (glyph or dot on
/// a subtle fill). Compact rows collapse categories to a dot and intelligence tiles to a round icon.
/// </summary>
public sealed record WinoMailRowTile(string Text, string? Glyph, UIColor? Background, UIColor? Foreground, bool IsIntelligence, bool IsWarning = false, string? Tooltip = null);

/// <summary>
/// Everything a <see cref="WinoMailRowCell"/> shows (UIKit twin of the AppKit <c>WinoMailRowModel</c>, same members).
/// The cell knows nothing about ViewModels; the page maps its mail item into this immutable snapshot.
/// </summary>
public sealed record WinoMailRowModel
{
    public WinoMailRowKind Kind { get; init; }
    public WinoMailRowDensity Density { get; init; } = WinoMailRowDensity.Medium;
    public string Sender { get; init; } = string.Empty;
    public string SenderAddress { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public string AccessibleDateText { get; init; } = string.Empty;
    public bool IsUnread { get; init; }
    public bool IsFlagged { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDraft { get; init; }
    public bool IsBusy { get; init; }
    public bool HasAttachments { get; init; }
    public IReadOnlyList<WinoMailRowTile> Tiles { get; init; } = Array.Empty<WinoMailRowTile>();
    public int ThreadCount { get; init; }
    public bool IsThreadExpanded { get; init; }
    public UIColor? AccountColor { get; init; }
    public string? AccountNickname { get; init; }
    public AccountNicknamePosition NicknamePosition { get; init; } = AccountNicknamePosition.None;
    public UIImage? Picture { get; init; }
    public bool ShowPicture { get; init; } = true;
    public bool ShowPreview { get; init; } = true;
    public string DraftLabel { get; init; } = "Draft";
    public string AccessibilityText { get; init; } = string.Empty;

    /// <summary>Detailed rows add a tile line under the preview when categories or intelligence tiles exist.</summary>
    public bool HasTileLine => Tiles.Count > 0 && Density != WinoMailRowDensity.Compact;

    public bool ShowNickname => !string.IsNullOrWhiteSpace(AccountNickname) && NicknamePosition != AccountNicknamePosition.None;

    /// <summary>
    /// Row height for a row whose text column is <paramref name="textWidth"/> wide (shared
    /// <see cref="WinoMailRowMetrics.HeightFor"/> with UIKit text measurement). Detailed rows add one tile line per
    /// wrapped line of tiles.
    /// </summary>
    public static double HeightFor(WinoMailRowDensity density, IReadOnlyList<WinoMailRowTile> tiles, double textWidth)
        => WinoMailRowMetrics.HeightFor(density, density == WinoMailRowDensity.Compact ? 0 : WinoMailRowMeasure.TileLines(tiles, textWidth), WinoMailRowMeasure.LineHeight);
}

/// <summary>
/// UIKit text measurement for the shared <see cref="WinoMailRowMetrics"/>: label line heights (scaled for the user's
/// text size) and detailed tile widths, measured once and cached per text size.
/// </summary>
public static class WinoMailRowMeasure
{
    private static readonly Dictionary<double, double> LineHeights = new();
    private static readonly Dictionary<(string Text, string Glyph, bool Intelligence), double> TileWidths = new();
    private static string? _contentSize;
    private static WinoChipView? _tilePrototype;

    /// <summary>The row font at a shared metrics point size, scaled with Dynamic Type.</summary>
    public static UIFont Font(double pointSize, UIFontWeight weight = UIFontWeight.Regular) => WinoStyle.Scaled(pointSize, weight);

    /// <summary>Lines the detailed tile row wraps onto in <paramref name="width"/> (same rule as <see cref="WinoFlowView"/>).</summary>
    public static int TileLines(IReadOnlyList<WinoMailRowTile> tiles, double width)
    {
        int count = Math.Min(tiles.Count, WinoMailRowMetrics.MaxTiles);
        var widths = new double[count];
        if (width > 0)
        {
            for (int index = 0; index < count; index++) widths[index] = TileWidth(tiles[index]);
        }
        return WinoMailRowMetrics.TileLines(widths, width);
    }

    /// <summary>Fitting width of a detailed tile, measured once per text and glyph on a prototype chip.</summary>
    public static double TileWidth(WinoMailRowTile tile)
    {
        var key = (tile.Text ?? string.Empty, tile.Glyph ?? string.Empty, tile.IsIntelligence);
        if (TileWidths.TryGetValue(key, out var cached)) return cached;
        if (TileWidths.Count > 512) TileWidths.Clear();
        _tilePrototype = WinoMailRowCell.DetailedTile(tile, _tilePrototype);
        double width = Math.Ceiling(_tilePrototype.FittingSize.Width);
        TileWidths[key] = width;
        return width;
    }

    /// <summary>True when two tile lists lay out the same (same texts and glyphs), whatever their colours.</summary>
    public static bool SameTileLayout(IReadOnlyList<WinoMailRowTile> left, IReadOnlyList<WinoMailRowTile> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (left[index].Text != right[index].Text || left[index].Glyph != right[index].Glyph || left[index].IsIntelligence != right[index].IsIntelligence)
                return false;
        return true;
    }

    /// <summary>Height of a one-line label at the given point size (scaled for the current text size).</summary>
    public static double LineHeight(double pointSize)
    {
        ResetOnTextSizeChange();
        if (LineHeights.TryGetValue(pointSize, out var cached)) return cached;
        double height = Math.Ceiling(Font(pointSize).LineHeight);
        LineHeights[pointSize] = height;
        return height;
    }

    /// <summary>Group header strip height (shared <see cref="WinoMailRowMetrics.GroupRowHeight"/>).</summary>
    public static double GroupRowHeight => WinoMailRowMetrics.GroupRowHeight(LineHeight(WinoMailRowMetrics.GroupHeaderFontSize));

    private static void ResetOnTextSizeChange()
    {
        var current = UITraitCollection.CurrentTraitCollection.PreferredContentSizeCategory.ToString();
        if (current == _contentSize) return;
        _contentSize = current;
        LineHeights.Clear();
        TileWidths.Clear();
    }
}
