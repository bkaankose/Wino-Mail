using System;
using System.Collections.Generic;

namespace Wino.Messaging.UI;

/// <summary>Defines whether an intelligence change targets messages, one mailbox, or the full database.</summary>
public enum IntelligenceMetadataChangeScope
{
    Messages,
    MailboxReset,
    DatabaseReset,
}

/// <summary>Notifies visible mail surfaces after committed local intelligence changes.</summary>
public sealed record IntelligenceMetadataChanged(
    Guid? LocalAccountId,
    IReadOnlySet<string> RemoteMessageIds,
    IntelligenceMetadataChangeScope Scope)
    : UIMessageBase<IntelligenceMetadataChanged>;
