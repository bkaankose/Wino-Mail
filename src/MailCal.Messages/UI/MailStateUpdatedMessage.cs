using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record MailStateUpdatedMessage(
    MailStateChange UpdatedState,
    EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailStateUpdatedMessage>;
