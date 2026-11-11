#nullable enable
using System;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>The encrypted agent brain stored for the Wino Account, as downloaded (opaque bytes and the server revision).</summary>
public sealed record WinoAgentBrainDownload(byte[] Payload, long Revision);

/// <summary>The server holds another revision of the brain than the one the upload expected (<c>AGENT_BRAIN_CONFLICT</c>).</summary>
public sealed class WinoAgentBrainConflictException(long currentRevision) : InvalidOperationException("The server holds a newer brain.")
{
    public long CurrentRevision { get; } = currentRevision;
}
