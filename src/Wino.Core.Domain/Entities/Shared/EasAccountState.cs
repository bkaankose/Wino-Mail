using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>The Exchange ActiveSync device partnership of one account: what the server knows this client by.</summary>
public sealed class EasAccountState
{
    [PrimaryKey]
    public Guid AccountId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string PolicyKey { get; set; } = "0";
    public string ProtocolVersion { get; set; } = string.Empty;
    public string ConfiguredEndpoint { get; set; } = string.Empty;
    public string ResolvedEndpoint { get; set; } = string.Empty;
    public bool IsRemoteWipeRequested { get; set; }

    /// <summary>Whether the limits below come from the policy the current <see cref="PolicyKey"/> was issued for.</summary>
    public bool IsPolicyRecorded { get; set; }

    /// <summary>The policy's MaxEmailAgeFilter as an AirSync FilterType; 0 is no limit.</summary>
    public int PolicyMaxEmailAgeFilter { get; set; }

    /// <summary>The policy's MaxCalendarAgeFilter as an AirSync FilterType; 0 is no limit.</summary>
    public int PolicyMaxCalendarAgeFilter { get; set; }

    public long? PolicyMaxAttachmentSize { get; set; }
    public int? PolicyMaxEmailBodyTruncationSize { get; set; }
    public int? PolicyMaxEmailHtmlBodyTruncationSize { get; set; }
    public bool PolicyAttachmentsDisabled { get; set; }
}
