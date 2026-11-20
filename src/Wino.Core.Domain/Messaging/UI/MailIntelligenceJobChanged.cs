using System;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Messaging.UI;

public record MailIntelligenceJobChanged(Guid AccountId, MailIntelligenceJobSnapshot Snapshot)
    : UIMessageBase<MailIntelligenceJobChanged>;
