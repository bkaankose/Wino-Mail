#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.AI.Cryptography;

namespace Wino.Services;

/// <summary>
/// The server transport key uploads are encrypted to. Fetched from the API so the server can
/// rotate it; the key embedded in the app is only a fallback for when the API cannot be asked.
/// </summary>
public class IntelligenceTransportKeyProvider(IWinoAccountApiClient apiClient)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<IntelligenceTransportKeyProvider>();
    private ContentEncryptionPublicKey? _cached;

    public virtual async Task<ContentEncryptionPublicKey> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cached) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            try
            {
                var dto = await apiClient.GetIntelligenceTransportKeyAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(dto.KeyId) && !string.IsNullOrWhiteSpace(dto.PublicKeyPem))
                {
                    _cached = new ContentEncryptionPublicKey(dto.KeyId, dto.PublicKeyPem);
                    return _cached;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Not cached: the next upload asks the API again.
                _logger.Warning(exception, "The intelligence transport key could not be fetched; using the embedded key.");
            }

            return EmbeddedIntelligencePublicKeyProvider.Load();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the cached key, after the server said it no longer knows it.</summary>
    public virtual void Invalidate() => Volatile.Write(ref _cached, null);
}
