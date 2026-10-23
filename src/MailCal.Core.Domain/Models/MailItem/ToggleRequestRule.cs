using System;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.MailItem;

/// <summary>
/// Defines a single rule for toggling user actions if needed.
/// For example: If user wants to mark a mail as read, but it's already read, then it should be marked as unread.
/// </summary>
/// <param name="SourceAction"></param>
/// <param name="TargetAction"></param>
/// <param name="Condition"></param>
public record ToggleRequestRule(MailOperation SourceAction, MailOperation TargetAction, Func<MailCopy, bool> Condition);
