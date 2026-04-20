using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record BulkMailRemovedMessage(IReadOnlyList<MailCopy> RemovedMails, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailRemovedMessage>;
