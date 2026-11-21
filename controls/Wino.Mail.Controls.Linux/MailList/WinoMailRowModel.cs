using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.MailRow;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>Everything a <see cref="WinoMailRow"/> shows. The row knows no ViewModel; the page maps its mail item into this snapshot.</summary>
public sealed record WinoMailRowModel
{
    public WinoMailRowKind Kind { get; init; }
    public WinoMailRowDensity Density { get; init; } = WinoMailRowDensity.Medium;
    public string Sender { get; init; } = string.Empty;
    public string SenderAddress { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public bool IsUnread { get; init; }
    public bool IsFlagged { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDraft { get; init; }
    public bool IsBusy { get; init; }
    public bool HasAttachments { get; init; }
    public int ThreadCount { get; init; }
    public bool IsThreadExpanded { get; init; }

    /// <summary>Null outside a merged view.</summary>
    public string? AccountColorHex { get; init; }
    public string? AccountNickname { get; init; }
    public AccountNicknamePosition NicknamePosition { get; init; } = AccountNicknamePosition.None;
    public Gdk.Texture? Picture { get; init; }
    public bool ShowPicture { get; init; } = true;
    public bool ShowPreview { get; init; } = true;
    public string AccessibilityText { get; init; } = string.Empty;

    public bool ShowNickname => !string.IsNullOrWhiteSpace(AccountNickname) && NicknamePosition != AccountNicknamePosition.None;
    public bool ShowChevron => Kind == WinoMailRowKind.ThreadHead && ThreadCount > 1;
}
