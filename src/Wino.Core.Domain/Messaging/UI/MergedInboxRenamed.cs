using System;

namespace Wino.Core.Domain.Messaging.UI;

public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : UIMessageBase<MergedInboxRenamed>;
