using System.Collections.Generic;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record BulkMailStateUpdatedMessage(
    IReadOnlyList<MailStateChange> UpdatedStates,
    EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailStateUpdatedMessage>;
