#nullable enable
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services.Assistant;

/// <summary>The brain blob endpoints (<c>/api/v1/users/me/brain</c>) as the runtime's sync transport. Revisions travel as strings; the server's are integers.</summary>
public sealed class AgentBrainSyncTransport(IWinoAccountApiClient apiClient) : IBrainSyncTransport
{
    public async Task<string?> GetRevisionAsync(CancellationToken ct)
    {
        var status = await apiClient.GetAgentBrainStatusAsync(ct).ConfigureAwait(false);
        return status?.Revision.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<BrainBlob?> DownloadAsync(CancellationToken ct)
    {
        var download = await apiClient.GetAgentBrainAsync(ct).ConfigureAwait(false);
        return download is null ? null : new BrainBlob(download.Payload, download.Revision.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<string> UploadAsync(byte[] blob, string? ifMatchRevision, CancellationToken ct)
    {
        long? expected = ifMatchRevision is not null && long.TryParse(ifMatchRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) ? revision : null;
        try
        {
            var status = await apiClient.PutAgentBrainAsync(blob, expected, ct).ConfigureAwait(false);
            return status.Revision.ToString(CultureInfo.InvariantCulture);
        }
        catch (WinoAgentBrainConflictException ex)
        {
            throw new BrainSyncConflictException(ex.CurrentRevision.ToString(CultureInfo.InvariantCulture));
        }
    }

    public Task DeleteAsync(CancellationToken ct) => apiClient.DeleteAgentBrainAsync(ct);
}
