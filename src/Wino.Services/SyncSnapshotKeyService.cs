#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services;

/// <summary>
/// Derives snapshot keys from the backup password. The password and the derived key are never
/// written anywhere.
/// </summary>
public sealed class SyncSnapshotKeyService : ISyncSnapshotKeyService
{
    // RFC 9106 second recommended profile. Written into every snapshot header, so they can change
    // later without a format change.
    public const int DefaultMemoryKiB = 65536;
    public const int DefaultIterations = 3;
    public const int DefaultParallelism = 4;

    // Earlier builds cached a key derived from the Wino Account password here.
    private const string LegacyFolderName = "sync-snapshot";

    private readonly string _legacyFolder;
    private readonly ILogger _logger = Log.ForContext<SyncSnapshotKeyService>();

    public SyncSnapshotKeyService(IApplicationConfiguration configuration)
    {
        _legacyFolder = Path.Combine(configuration.ApplicationDataFolderPath, LegacyFolderName);
    }

    public SyncSnapshotKeyParameters CreateParameters()
        => new(
            SyncSnapshotFormat.KeySourcePassphrase,
            DefaultMemoryKiB,
            DefaultIterations,
            DefaultParallelism,
            SyncSnapshotCryptography.CreateSalt());

    public async Task<SyncSnapshotKey> DeriveAsync(string secret, SyncSnapshotKeyParameters parameters, CancellationToken cancellationToken = default)
    {
        var key = await SyncSnapshotCryptography.DeriveKeyAsync(secret, parameters, cancellationToken).ConfigureAwait(false);

        return new SyncSnapshotKey(parameters, key);
    }

    public void DeleteLegacyKeyCache()
    {
        try
        {
            if (Directory.Exists(_legacyFolder)) Directory.Delete(_legacyFolder, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Legacy sync snapshot key cache could not be removed.");
        }
    }
}
