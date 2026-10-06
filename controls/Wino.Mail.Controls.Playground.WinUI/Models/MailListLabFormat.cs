using System.Globalization;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.Playground.Models;

/// <summary>x:Bind helpers for the mail list interactions page templates.</summary>
public static class MailListLabFormat
{
    public static MailListLabItem Item(IMailListSourceItem item) => (MailListLabItem)item;

    public static IContactPicture ContactPicture(IMailListSourceItem item) => (IContactPicture)item;

    public static string ChevronGlyph(bool isExpanded) => isExpanded ? WinoIconCodes.ChevronDown : WinoIconCodes.ChevronRight;

    public static string ThreadCount(MailListThread? thread) => thread is null ? string.Empty : thread.Count.ToString(CultureInfo.InvariantCulture);

    public static double UnreadOpacity(bool isRead) => isRead ? 0 : 1;

    /// <summary>Formats a projection group key the way the list groups it: a date, a letter, or pinned.</summary>
    public static string GroupHeader(object? key) => key switch
    {
        MailListProjectionGroupKey { IsPinned: true } => "Pinned",
        MailListProjectionGroupKey { Value: DateTime date } => FormatDate(date),
        MailListProjectionGroupKey { Value: string letter } => letter,
        MailListProjectionGroupKey { Value: { } value } => value.ToString() ?? string.Empty,
        string text => text,
        _ => string.Empty,
    };

    private static string FormatDate(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    }
}
