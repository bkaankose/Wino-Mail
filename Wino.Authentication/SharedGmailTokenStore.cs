using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Util.Store;

namespace Wino.Authentication;

/// <summary>
/// Gmail token store that lives in the publisher shared folder so both the UI process
/// and the background companion process can read and refresh the same credentials.
/// All operations are guarded by a named cross-process semaphore because Google's
/// <see cref="FileDataStore"/> has no locking of its own.
/// Existing tokens from the legacy %AppData% store are migrated on first use.
/// </summary>
public sealed class SharedGmailTokenStore : IDataStore
{
    private const string CrossProcessLockName = @"Local\WinoGmailTokenStoreLock";
    private const string SharedStoreFolderName = "GmailTokenStore";

    private readonly FileDataStore _innerStore;
    private readonly string _storeFolderPath;
    private readonly string _legacyStoreIdentifier;

    public SharedGmailTokenStore(string publisherSharedFolderPath, string legacyStoreIdentifier)
    {
        _storeFolderPath = Path.Combine(publisherSharedFolderPath, SharedStoreFolderName);
        _legacyStoreIdentifier = legacyStoreIdentifier;

        MigrateLegacyStoreIfNeeded();

        _innerStore = new FileDataStore(_storeFolderPath, fullPath: true);
    }

    public Task StoreAsync<T>(string key, T value) => WithLockAsync(() => _innerStore.StoreAsync(key, value));

    public Task DeleteAsync<T>(string key) => WithLockAsync(() => _innerStore.DeleteAsync<T>(key));

    public Task<T> GetAsync<T>(string key) => WithLockAsync(() => _innerStore.GetAsync<T>(key));

    public Task ClearAsync() => WithLockAsync(() => _innerStore.ClearAsync());

    private async Task WithLockAsync(Func<Task> action)
    {
        using var semaphore = new Semaphore(1, 1, CrossProcessLockName, out _);
        semaphore.WaitOne(TimeSpan.FromSeconds(30));

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        using var semaphore = new Semaphore(1, 1, CrossProcessLockName, out _);
        semaphore.WaitOne(TimeSpan.FromSeconds(30));

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void MigrateLegacyStoreIfNeeded()
    {
        try
        {
            if (Directory.Exists(_storeFolderPath) && Directory.GetFiles(_storeFolderPath).Length > 0)
                return;

            // FileDataStore with a non-rooted identifier resolves to %AppData%\{identifier}.
            var legacyFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _legacyStoreIdentifier);

            if (!Directory.Exists(legacyFolderPath))
                return;

            Directory.CreateDirectory(_storeFolderPath);

            foreach (var legacyFile in Directory.GetFiles(legacyFolderPath))
            {
                var targetPath = Path.Combine(_storeFolderPath, Path.GetFileName(legacyFile));

                if (!File.Exists(targetPath))
                {
                    File.Copy(legacyFile, targetPath);
                }
            }
        }
        catch
        {
            // Migration is best-effort; a failed copy only means the user re-authenticates.
        }
    }
}
