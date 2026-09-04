using System;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public sealed record SemanticIndexAccountState(
    bool IsEnabled,
    Guid? ServerMailboxId,
    MailboxIntelligenceHeadDto? ServerHead,
    LocalIntelligenceMailboxState? LocalState,
    int WaitingMessageCount,
    bool HasEligibleMessages,
    bool IsUpToDate,
    bool CanDownload,
    int LocalIndexedMessageCount = 0)
{
    public long LastImportedVersion => LocalState?.LastImportedRevision ?? 0;
}
