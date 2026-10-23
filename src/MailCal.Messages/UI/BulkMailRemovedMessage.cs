using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record BulkMailRemovedMessage(IReadOnlyList<MailCopy> RemovedMails, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailRemovedMessage>;
