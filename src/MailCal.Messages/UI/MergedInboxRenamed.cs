using System;

namespace MailCal.Messaging.UI;

public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : UIMessageBase<MergedInboxRenamed>;
