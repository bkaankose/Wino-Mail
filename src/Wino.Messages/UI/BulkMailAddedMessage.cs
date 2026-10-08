using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record BulkMailAddedMessage(IReadOnlyList<MailCopy> AddedMails, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailAddedMessage>;
