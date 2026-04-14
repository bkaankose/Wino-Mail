using System.Collections.Generic;

namespace Wino.Core.Domain.Models.MailItem;

public static class MailCategoryPalette
{
    public static IReadOnlyList<MailCategoryColorOption> DefaultOptions { get; } =
    [
        new("#FEE2E2", "#991B1B"),
        new("#FECACA", "#7F1D1D"),
        new("#FFEDD5", "#9A3412"),
        new("#FED7AA", "#7C2D12"),
        new("#FEF3C7", "#92400E"),
        new("#FDE68A", "#78350F"),
        new("#ECFCCB", "#3F6212"),
        new("#D9F99D", "#365314"),
        new("#DCFCE7", "#166534"),
        new("#BBF7D0", "#14532D"),
        new("#CCFBF1", "#115E59"),
        new("#99F6E4", "#134E4A"),
        new("#CFFAFE", "#155E75"),
        new("#A5F3FC", "#164E63"),
        new("#DBEAFE", "#1D4ED8"),
        new("#BFDBFE", "#1E3A8A"),
        new("#E0E7FF", "#4338CA"),
        new("#DDD6FE", "#5B21B6"),
        new("#F3E8FF", "#7E22CE"),
        new("#FCE7F3", "#9D174D")
    ];
}
