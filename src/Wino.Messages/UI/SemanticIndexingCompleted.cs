using System;

namespace Wino.Messaging.UI;

public record SemanticIndexingCompleted(
    Guid AccountId,
    string AccountAddress,
    int IndexedMessageCount)
    : UIMessageBase<SemanticIndexingCompleted>;
