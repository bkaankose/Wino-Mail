using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record BulkMailUpdatedMessage(IReadOnlyList<MailCopy> UpdatedMails) : UIMessageBase<BulkMailUpdatedMessage>;
