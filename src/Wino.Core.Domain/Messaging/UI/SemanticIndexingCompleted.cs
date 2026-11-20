using System;

namespace Wino.Core.Domain.Messaging.UI;

public record SemanticIndexingCompleted(
    Guid AccountId,
    string AccountAddress,
    int IndexedMessageCount)
    : UIMessageBase<SemanticIndexingCompleted>;
