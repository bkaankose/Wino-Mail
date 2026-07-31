using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record BulkMailUpdatedMessage(
    IReadOnlyList<MailCopy> UpdatedMails,
    EntityUpdateSource Source = EntityUpdateSource.Server,
    MailCopyChangeFlags ChangedProperties = MailCopyChangeFlags.None) : UIMessageBase<BulkMailUpdatedMessage>;
