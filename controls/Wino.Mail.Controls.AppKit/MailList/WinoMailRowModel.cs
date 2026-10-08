using AppKit;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Controls.AppKit.MailList;

public enum WinoMailRowDensity
{
    Compact,
    Medium,
    Spacious
}

public enum WinoMailRowKind
{
    Single,
    ThreadHead,
    ThreadChild
}

/// <summary>
/// One tile on a mail row (the Windows RowTiles): a category (coloured, text) or an intelligence
/// tile (glyph or dot on a subtle fill). Compact rows collapse categories to a dot and
/// intelligence tiles to a round icon.
/// </summary>
public sealed record WinoMailRowTile(string Text, string? Glyph, NSColor? Background, NSColor? Foreground, bool IsIntelligence, bool IsWarning = false, string? Tooltip = null);

/// <summary>
/// Everything a <see cref="WinoMailRowView"/> shows. The row view knows nothing about
/// ViewModels; the page maps its mail item into this immutable snapshot.
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
    public IReadOnlyList<WinoMailRowTile> Tiles { get; init; } = [];
    public int ThreadCount { get; init; }
    public bool IsThreadExpanded { get; init; }
    public NSColor? AccountColor { get; init; }
    public string? AccountNickname { get; init; }
    public AccountNicknamePosition NicknamePosition { get; init; } = AccountNicknamePosition.None;
    public NSImage? Picture { get; init; }
    public bool ShowPicture { get; init; } = true;
    public bool ShowPreview { get; init; } = true;
    public string DraftLabel { get; init; } = "Draft";
    public string AccessibilityText { get; init; } = string.Empty;

    /// <summary>Detailed rows add a tile line under the preview when categories or intelligence tiles exist.</summary>
    public bool HasTileLine => Tiles.Count > 0 && Density != WinoMailRowDensity.Compact;

    public bool ShowNickname => !string.IsNullOrWhiteSpace(AccountNickname) && NicknamePosition != AccountNicknamePosition.None;

    /// <summary>
    /// Row height for a row whose text column is <paramref name="textWidth"/> wide: the Windows template's
    /// MinHeight, or the content plus the inner padding and the row gap when taller. Detailed rows add
    /// one tile line per wrapped line of tiles (<see cref="WinoMailRowMetrics.TileLines"/>).
    /// </summary>
    public static double HeightFor(WinoMailRowDensity density, IReadOnlyList<WinoMailRowTile> tiles, double textWidth)
    {
        double content = WinoMailRowMetrics.LineHeight(13) + WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.LineHeight(13);
        if (density != WinoMailRowDensity.Compact)
        {
            content += WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.LineHeight(12);
            int lines = WinoMailRowMetrics.TileLines(tiles, textWidth);
            if (lines > 0)
                content += WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.TileLineTopMargin(density)
                    + lines * WinoMailRowMetrics.TileHeight + (lines - 1) * WinoMailRowMetrics.TileLineSpacing;
        }
        double chrome = 2 * (WinoMailRowMetrics.Padding(density) + WinoMailRowMetrics.RowGap);
        return Math.Max(WinoMailRowMetrics.MinHeight(density), Math.Ceiling(content + chrome));
    }
}

/// <summary>
/// Measurements shared by the row view, its row container and the table's height delegate
/// (Windows MailListItemTemplates). Horizontally a row is: <see cref="ContentLeading"/> to the
/// highlight box, inside it the selection pill at <see cref="PillInset"/>, the content at
/// <see cref="InnerLeading"/> and <see cref="InnerTrailing"/> before the box's trailing edge.
/// Vertically the box is inset by <see cref="RowGap"/> so neighbouring highlights do not touch,
/// and the content keeps <see cref="Padding"/> from the box edges.
/// </summary>
public static class WinoMailRowMetrics
{
    public const double LineSpacing = 2;
    public const double TileHeight = 18;
    public const double TileSpacing = 4;
    public const double TileLineSpacing = 4;
    public const int MaxTiles = 6;
    public const double PillInset = 3;
    public const double PillWidth = 3;
    public const double ContentLeading = 4;
    public const double ContentTrailing = 4;
    public const double InnerLeading = 12;
    public const double InnerTrailing = 10;
    public const double RowGap = 2;
    public const double AvatarSpacing = 8;
    public const double CheckboxWidth = 16;
    public const double CheckboxSpacing = 6;

    private static readonly Dictionary<double, double> LineHeights = new();
    private static readonly Dictionary<(string Text, string Glyph, bool Intelligence), double> TileWidths = new();
    private static WinoChipView? _tilePrototype;

    public static double MinHeight(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 56,
        WinoMailRowDensity.Spacious => 88,
        _ => 76
    };

    /// <summary>Space between the highlight box edge and the content, above and below.</summary>
    public static double Padding(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 6,
        WinoMailRowDensity.Spacious => 10,
        _ => 7
    };

    public static double AvatarSize(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 28,
        WinoMailRowDensity.Spacious => 34,
        _ => 30
    };

    public static double ChildIndent(WinoMailRowDensity density) => density == WinoMailRowDensity.Compact ? 16 : 20;

    public static double TileLineTopMargin(WinoMailRowDensity density) => density == WinoMailRowDensity.Spacious ? 4 : 2;

    /// <summary>
    /// Width of the text column (the three lines and the tile line) in a row <paramref name="rowWidth"/> wide.
    /// Mirrors the row view's constraints; it errs on the narrow side so a predicted wrap is never missed.
    /// </summary>
    public static double TextWidth(double rowWidth, WinoMailRowDensity density, WinoMailRowKind kind, bool showPicture, bool selectionMode)
    {
        double width = rowWidth - ContentLeading - ContentTrailing - InnerLeading - InnerTrailing;
        if (kind == WinoMailRowKind.ThreadChild) width -= ChildIndent(density);
        if (showPicture) width -= AvatarSize(density) + AvatarSpacing;
        if (selectionMode) width -= CheckboxWidth + CheckboxSpacing;
        return width - 1;
    }

    /// <summary>
    /// Lines the detailed tile row wraps onto in <paramref name="width"/>, with the same rule as
    /// <see cref="WinoFlowView"/>: a tile that does not fit after another one starts a new line.
    /// 0 when there are no tiles; 1 when the width is not known yet.
    /// </summary>
    public static int TileLines(IReadOnlyList<WinoMailRowTile> tiles, double width)
    {
        int count = Math.Min(tiles.Count, MaxTiles);
        if (count == 0) return 0;
        if (width <= 0) return 1;
        int lines = 1;
        double x = 0;
        for (int index = 0; index < count; index++)
        {
            double tileWidth = Math.Min(TileWidth(tiles[index]), width);
            if (x > 0 && x + tileWidth > width)
            {
                lines++;
                x = 0;
            }
            x += tileWidth + TileSpacing;
        }
        return lines;
    }

    /// <summary>Fitting width of a detailed tile, measured once per text and glyph on a prototype chip.</summary>
    public static double TileWidth(WinoMailRowTile tile)
    {
        var key = (tile.Text ?? string.Empty, tile.Glyph ?? string.Empty, tile.IsIntelligence);
        if (TileWidths.TryGetValue(key, out var cached)) return cached;
        if (TileWidths.Count > 512) TileWidths.Clear();
        _tilePrototype = (WinoChipView)WinoMailRowView.DetailedTile(tile, _tilePrototype);
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

    /// <summary>Height of a one-line label at the given system font size, as AppKit lays it out.</summary>
    public static double LineHeight(double pointSize)
    {
        if (LineHeights.TryGetValue(pointSize, out var cached)) return cached;
        var label = NSTextField.CreateLabel("Ag");
        label.Font = NSFont.SystemFontOfSize((nfloat)pointSize);
        double height = Math.Ceiling(label.IntrinsicContentSize.Height);
        LineHeights[pointSize] = height;
        return height;
    }

    /// <summary>Group header strip: 7×12 padding around 11pt text, plus the 6pt gap under it.</summary>
    public static double GroupRowHeight => Math.Ceiling(LineHeight(11) + 14 + 6);
}
