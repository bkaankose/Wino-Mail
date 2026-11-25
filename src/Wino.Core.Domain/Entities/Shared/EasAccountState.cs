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
}
