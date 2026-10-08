using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

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
