using System.Threading;
using System.Threading.Tasks;

namespace Wino.Authentication;

/// <summary>Stores shared serialized token bytes; missing returns null and failures propagate.</summary>
public interface IGoogleTokenStore
{
    Task<byte[]> ReadAsync(string credentialKey, CancellationToken cancellationToken = default);
    Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default);
}
