using Wino.Services.Security;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// Creates the protection key of a new installation when the user starts setup, not at launch: on a desktop without a
/// default keyring the first write opens the system's "new keyring" password prompt, which must not appear over an
/// empty window. The bootstrap writes the marker only after it verified a new installation, so the key is still
/// created only for an installation that has nothing protected yet.
/// </summary>
public sealed class LinuxProtectionKeySetup
{
    public const string MarkerFileName = "protection-key.pending";

    private readonly string _marker;
    private readonly AesGcmEnvelopeSecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LinuxProtectionKeySetup(string marker, AesGcmEnvelopeSecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        ArgumentNullException.ThrowIfNull(protector);
        _marker = marker;
        _protector = protector;
    }

    public bool IsPending => File.Exists(_marker);

    /// <summary>False when the user dismissed the keyring prompt or no Secret Service is available.</summary>
    public async Task<bool> EnsureKeyAsync()
    {
        if (!IsPending) return true;
        await _gate.WaitAsync();
        try
        {
            if (!IsPending) return true;
            // libsecret's synchronous calls wait for the prompt, so they stay off the GTK main loop.
            await Task.Run(() => _protector.ProvisionNewInstallationKey(true));
            File.Delete(_marker);
            Serilog.Log.Information("The protection key was created in the keyring.");
            return true;
        }
        catch (Exception exception) when (exception is SecretServiceException or DllNotFoundException)
        {
            Serilog.Log.Warning("The keyring did not store the protection key: {Message}", exception.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
