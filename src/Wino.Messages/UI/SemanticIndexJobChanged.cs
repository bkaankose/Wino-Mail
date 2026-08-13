using System;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Messaging.UI;

public record SemanticIndexJobChanged(Guid AccountId, SemanticIndexJobSnapshot Snapshot)
    : UIMessageBase<SemanticIndexJobChanged>;
