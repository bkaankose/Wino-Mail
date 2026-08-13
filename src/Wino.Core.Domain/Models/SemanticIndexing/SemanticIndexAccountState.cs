using System;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public sealed record SemanticIndexAccountState(
    bool IsEnabled,
    Guid? ServerMailboxId,
    SemanticMailboxIndexStateDto? ServerIndex,
    long LastImportedVersion,
    int WaitingMessageCount,
    bool HasEligibleMessages,
    bool IsUpToDate,
    bool CanDownload,
    int LocalIndexedMessageCount = 0,
    IntelligenceMailboxStateDto? IntelligenceState = null);
