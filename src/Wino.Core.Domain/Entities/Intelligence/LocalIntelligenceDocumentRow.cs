using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalIntelligenceDocument")]
public sealed class LocalIntelligenceDocumentRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    public Guid MailboxId { get; set; }

    [Indexed]
    public string ServerMessageKey { get; set; } = string.Empty;

    public string IntelligenceVersion { get; set; } = string.Empty;
    public string IndexEpoch { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public bool IsOutgoing { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool HasAttachments { get; set; }
    public bool IsDirectRecipient { get; set; }
    public bool HasLaterOutgoingReply { get; set; }
    public string Importance { get; set; } = "normal";
    public string AttachmentMetadataJson { get; set; } = "[]";
    public string FolderIdsJson { get; set; } = "[]";
    public string SenderAddressesJson { get; set; } = "[]";
    public string RecipientAddressesJson { get; set; } = "[]";
    public string AnalysisJson { get; set; } = "{}";
    public byte[] Embedding { get; set; } = [];
    public int EmbeddingDimensions { get; set; }
    public string EmbeddingEncoding { get; set; } = string.Empty;

    [Indexed]
    public long ArtifactRevision { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
