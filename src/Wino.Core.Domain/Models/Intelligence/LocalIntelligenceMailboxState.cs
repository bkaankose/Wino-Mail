using System;
using System.Collections.Generic;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record LocalIntelligenceMailboxState(
    Guid LocalAccountId,
    Guid MailboxId,
    string IntelligenceVersion,
    Guid IndexEpoch,
    long LastImportedRevision);

public sealed record LocalIntelligenceChangeApplyResult(
    IReadOnlySet<string> UpsertedServerMessageKeys,
    IReadOnlySet<string> DeletedServerMessageKeys,
    long ThroughRevision);

public sealed record LocalIntelligenceSearchDocument(
    Guid LocalAccountId,
    Guid MailboxId,
    MessageIntelligenceDownloadDto Document,
    byte[] EmbeddingBytes);
