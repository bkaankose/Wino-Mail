namespace Wino.Core.Domain.Models.Accounts;

/// <summary>Outcome of a MAPI/HTTP connection probe. Never carries a token, a cookie or mailbox content.</summary>
public sealed record MapiProbeResult(
    bool Succeeded,
    string Stage,
    string Detail,
    string DisplayName = null,
    string AuthMode = null,
    string Endpoint = null,
    string LegacyDn = null,
    int FolderCount = 0,
    long ElapsedMs = 0);
