using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record BulkMailAddedMessage(IReadOnlyList<MailCopy> AddedMails, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailAddedMessage>;
