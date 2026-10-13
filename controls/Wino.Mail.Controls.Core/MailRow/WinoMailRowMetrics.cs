namespace Wino.Mail.Controls.Core;

/// <summary>Mail list row density (the Windows MailListDisplayMode).</summary>
public enum WinoMailRowDensity
{
    Compact,
    Medium,
    Spacious
}

/// <summary>Where a mail row sits in a conversation.</summary>
public enum WinoMailRowKind
{
    Single,
    ThreadHead,
    ThreadChild
}

/// <summary>
/// Platform-neutral measurements of a native mail row (Windows MailListItemTemplates). Horizontally a
/// row is: <see cref="ContentLeading"/> to the highlight box, inside it the selection pill at
/// <see cref="PillInset"/>, the content at <see cref="InnerLeading"/> and <see cref="InnerTrailing"/>
/// before the box's trailing edge. Vertically the box is inset by <see cref="RowGap"/> so neighbouring
/// highlights do not touch, and the content keeps <see cref="Padding"/> from the box edges.
/// Text measurement stays with the platform: callers pass a line-height delegate and tile widths.
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

    /// <summary>Point size of the sender and subject lines.</summary>
    public const double PrimaryFontSize = 13;

    /// <summary>Point size of the preview line.</summary>
    public const double PreviewFontSize = 12;

    /// <summary>Point size of the group header text.</summary>
    public const double GroupHeaderFontSize = 11;

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
    /// Lines the detailed tile row wraps onto in <paramref name="width"/>: a tile that does not fit after
    /// another one starts a new line, and at most <see cref="MaxTiles"/> tiles are shown. 0 when there are
    /// no tiles; 1 when the width is not known yet.
    /// </summary>
    public static int TileLines(IReadOnlyList<double> tileWidths, double width)
    {
        ArgumentNullException.ThrowIfNull(tileWidths);

        int count = Math.Min(tileWidths.Count, MaxTiles);
        if (count == 0) return 0;
        if (width <= 0) return 1;

        int lines = 1;
        double x = 0;
        for (int index = 0; index < count; index++)
        {
            double tileWidth = Math.Min(tileWidths[index], width);
            if (x > 0 && x + tileWidth > width)
            {
                lines++;
                x = 0;
            }

            x += tileWidth + TileSpacing;
        }

        return lines;
    }

    /// <summary>
    /// Row height: the Windows template's MinHeight, or the content plus the inner padding and the row gap
    /// when taller. Detailed rows add the preview line and <paramref name="tileLines"/> wrapped tile lines.
    /// <paramref name="lineHeight"/> returns the platform's one-line label height for a point size.
    /// </summary>
    public static double HeightFor(WinoMailRowDensity density, int tileLines, Func<double, double> lineHeight)
    {
        ArgumentNullException.ThrowIfNull(lineHeight);

        double content = lineHeight(PrimaryFontSize) + LineSpacing + lineHeight(PrimaryFontSize);
        if (density != WinoMailRowDensity.Compact)
        {
            content += LineSpacing + lineHeight(PreviewFontSize);
            if (tileLines > 0)
                content += LineSpacing + TileLineTopMargin(density) + tileLines * TileHeight + (tileLines - 1) * TileLineSpacing;
        }

        double chrome = 2 * (Padding(density) + RowGap);
        return Math.Max(MinHeight(density), Math.Ceiling(content + chrome));
    }

    /// <summary>Group header strip: 7×12 padding around the header text, plus the 6pt gap under it.</summary>
    public static double GroupRowHeight(double headerLineHeight) => Math.Ceiling(headerLineHeight + 14 + 6);
}
