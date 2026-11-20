using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Messaging.UI;

public record BulkMailStateUpdatedMessage(
    IReadOnlyList<MailStateChange> UpdatedStates,
    EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<BulkMailStateUpdatedMessage>;
