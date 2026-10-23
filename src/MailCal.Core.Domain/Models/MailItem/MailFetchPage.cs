using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.MailItem;

/// <summary>
/// Opaque continuation for a stable, keyset-paged mail query.
/// Consumers should only retain and pass the value back to IMailService.
/// </summary>
public sealed record MailFetchCursor(
    SortingOptionType SortingOptionType,
    DateTime CreationDate,
    string SenderName,
    Guid UniqueId);

public sealed record MailFetchPage(
    IReadOnlyList<MailCopy> Items,
    MailFetchCursor NextCursor,
    bool HasMore);
