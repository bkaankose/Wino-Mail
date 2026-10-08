using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record MailStateUpdatedMessage(
    MailStateChange UpdatedState,
    EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailStateUpdatedMessage>;
