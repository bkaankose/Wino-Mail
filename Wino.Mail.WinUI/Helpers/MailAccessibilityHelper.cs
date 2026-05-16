using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Helpers;

public static class MailAccessibilityHelper
{
    public static string GetMailListItemName(IMailItemDisplayInformation? mailItem, int? threadItemCount = null)
    {
        if (mailItem == null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (threadItemCount.HasValue)
        {
            parts.Add(string.Format(Translator.Accessibility_MailThreadMessageCount, threadItemCount.Value));
            parts.Add(mailItem.IsThreadExpanded ? Translator.Accessibility_MailThreadExpanded : Translator.Accessibility_MailThreadCollapsed);
        }

        parts.Add(mailItem.IsRead ? Translator.Accessibility_MailItemReadState_Read : Translator.Accessibility_MailItemReadState_Unread);

        if (mailItem.IsDraft)
        {
            parts.Add(Translator.Draft);
        }

        if (mailItem.IsFlagged)
        {
            parts.Add(Translator.Accessibility_MailItemFlagged);
        }

        if (mailItem.HasAttachments)
        {
            parts.Add(Translator.Accessibility_MailItemHasAttachments);
        }

        parts.Add(GetSubject(mailItem.Subject));
        parts.Add(GetSender(mailItem));
        parts.Add(mailItem.CreationDate.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));

        if (!string.IsNullOrWhiteSpace(mailItem.PreviewText))
        {
            parts.Add(mailItem.PreviewText);
        }

        return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string GetSubject(string? subject)
        => string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject.Trim();

    private static string GetSender(IMailItemDisplayInformation mailItem)
    {
        if (!string.IsNullOrWhiteSpace(mailItem.FromName))
        {
            return mailItem.FromName.Trim();
        }

        return string.IsNullOrWhiteSpace(mailItem.FromAddress)
            ? Translator.UnknownSender
            : mailItem.FromAddress.Trim();
    }
}
