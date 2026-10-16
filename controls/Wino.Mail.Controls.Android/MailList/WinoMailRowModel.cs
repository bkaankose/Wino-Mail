using Android.Graphics;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.Android.MailList;

/// <summary>
/// One tile on a mail row (the Windows RowTiles): a category (coloured, text) or an intelligence tile (glyph or dot on a
/// subtle fill). Compact rows collapse categories to a dot and intelligence tiles to a round icon.
/// </summary>
public sealed record WinoMailRowTile(string Text, string? Glyph, Color? Background, Color? Foreground, bool IsIntelligence, bool IsWarning = false, string? Tooltip = null);

/// <summary>
/// Everything a <see cref="WinoMailRowView"/> shows (Android twin of the UIKit and AppKit <c>WinoMailRowModel</c>, same
/// members). The view knows nothing about ViewModels; the page maps its row (shared <c>MailRowPresentation.Describe</c>)
/// into this immutable snapshot and adds colours and the picture.
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
    public Color? AccountColor { get; init; }
    public string? AccountNickname { get; init; }
    public AccountNicknamePosition NicknamePosition { get; init; } = AccountNicknamePosition.None;
    public Bitmap? Picture { get; init; }
    public bool ShowPicture { get; init; } = true;
    public bool ShowPreview { get; init; } = true;
    public string DraftLabel { get; init; } = "Draft";
    public string AccessibilityText { get; init; } = string.Empty;

    /// <summary>Detailed rows add a tile line under the preview when categories or intelligence tiles exist.</summary>
    public bool HasTileLine => Tiles.Count > 0 && Density != WinoMailRowDensity.Compact;

    public bool ShowNickname => !string.IsNullOrWhiteSpace(AccountNickname) && NicknamePosition != AccountNicknamePosition.None;
}

/// <summary>
/// Android sizes of the mail row per density in dp (canvas Tokens "Mail row 76 min height"; Components 40 dp avatar,
/// 16/14/12 sp lines). Indents, pill and tile sizes come from the shared <see cref="WinoMailRowMetrics"/>.
/// </summary>
public static class WinoMailRowSizes
{
    public static double MinHeight(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 60,
        WinoMailRowDensity.Spacious => 88,
        _ => 76
    };

    public static double AvatarSize(WinoMailRowDensity density) => density == WinoMailRowDensity.Compact ? 32 : 40;

    /// <summary>The gap after the avatar (wino_mail_row.xml), also used when the select check takes the avatar's place.</summary>
    public const double AvatarGap = 12;

    /// <summary>The round select check before the text when the row has no sender picture.</summary>
    public const double SelectCheckSize = 28;
    public const double SelectCheckGap = 8;
    public const double SelectCheckGlyph = 20;

    public static double VerticalPadding(WinoMailRowDensity density) => density switch
    {
        WinoMailRowDensity.Compact => 6,
        WinoMailRowDensity.Spacious => 12,
        _ => 8
    };
}
