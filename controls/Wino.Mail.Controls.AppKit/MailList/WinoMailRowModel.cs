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

    /// <summary>Row height: the Windows template's MinHeight, or the content plus padding when taller.</summary>
    public static double HeightFor(WinoMailRowDensity density, bool hasTileLine)
    {
        double padding = WinoMailRowMetrics.Padding(density);
        double content = WinoMailRowMetrics.LineHeight(13) + WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.LineHeight(13);
        if (density != WinoMailRowDensity.Compact)
        {
            content += WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.LineHeight(12);
            if (hasTileLine) content += WinoMailRowMetrics.TileLineTopMargin(density) + WinoMailRowMetrics.TileHeight;
        }
        return Math.Max(WinoMailRowMetrics.MinHeight(density), Math.Ceiling(content + padding * 2));
    }
}

/// <summary>Measurements shared by the row view and the table's height delegate (Windows MailListItemTemplates).</summary>
public static class WinoMailRowMetrics
{
    public const double LineSpacing = 2;
    public const double TileHeight = 18;
    public const double Gutter = 4;
    public const double PillWidth = 4;
    public const double ContentLeading = 12;
    public const double ContentTrailing = 4;

    private static readonly Dictionary<double, double> LineHeights = new();

    public static double MinHeight(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 56,
        WinoMailRowDensity.Spacious => 88,
        _ => 76
    };

    public static double Padding(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 4,
        WinoMailRowDensity.Spacious => 8,
        _ => 6
    };

    public static double AvatarSize(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 28,
        WinoMailRowDensity.Spacious => 36,
        _ => 30
    };

    public static double ChildIndent(WinoMailRowDensity density) => density == WinoMailRowDensity.Compact ? 20 : 24;

    public static double TileLineTopMargin(WinoMailRowDensity density) => density == WinoMailRowDensity.Spacious ? 4 : 2;

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
