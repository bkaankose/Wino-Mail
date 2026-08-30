using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Services;

public sealed class AuthenticationTokenMigrationService(
    IApplicationConfiguration configuration) : IAuthenticationTokenMigrationService
{
    public async Task<AuthenticationTokenMigrationResult> PrepareAsync(
        IReadOnlyCollection<MigrationAccountOptions> accounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var outlookMigrated = await PrepareOutlookCacheAsync(cancellationToken).ConfigureAwait(false);
        var reusableGmailAccounts = new List<Guid>();

        foreach (var account in accounts.Where(account => account.ProviderType == MailProviderType.Gmail))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await PrepareGmailTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false))
                reusableGmailAccounts.Add(account.AccountId);
        }

        return new AuthenticationTokenMigrationResult(outlookMigrated, reusableGmailAccounts);
    }

    public async Task FinalizeAsync(
        IReadOnlyCollection<MigrationAccountOptions> accounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        await PrepareAsync(accounts, cancellationToken).ConfigureAwait(false);

        var localOutlookPath = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(configuration);
        var legacyOutlookPath = AuthenticationTokenStorePaths.GetLegacyOutlookTokenCachePath(configuration);
        if (!PathsEqual(localOutlookPath, legacyOutlookPath) && File.Exists(legacyOutlookPath))
        {
            await EnsureFilesMatchAsync(localOutlookPath, legacyOutlookPath, cancellationToken).ConfigureAwait(false);
            File.Delete(legacyOutlookPath);
        }

        foreach (var account in accounts.Where(account => account.ProviderType == MailProviderType.Gmail))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localTokenPath = AuthenticationTokenStorePaths.GetGmailTokenPath(configuration, account.AccountId);
            foreach (var legacyStorePath in AuthenticationTokenStorePaths.GetLegacyGmailTokenStorePaths(configuration))
            {
                var legacyCurrentPath = Path.Combine(legacyStorePath, $"{account.AccountId:N}.json");
                if (!PathsEqual(localTokenPath, legacyCurrentPath))
                    DeleteIfExists(legacyCurrentPath);

                DeleteIfExists(AuthenticationTokenStorePaths.GetLegacyGoogleTokenPath(legacyStorePath, account.AccountId));
            }
        }

        var publisherTokenStorePath = AuthenticationTokenStorePaths
            .GetLegacyPublisherGmailTokenStorePath(configuration);
        if (!PathsEqual(
                publisherTokenStorePath,
                AuthenticationTokenStorePaths.GetGmailTokenStorePath(configuration)) &&
            Directory.Exists(publisherTokenStorePath))
        {
            Directory.Delete(publisherTokenStorePath, recursive: true);
        }
    }

    private async Task<bool> PrepareOutlookCacheAsync(CancellationToken cancellationToken)
    {
        var localPath = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(configuration);
        if (File.Exists(localPath))
            return true;

        var legacyPath = AuthenticationTokenStorePaths.GetLegacyOutlookTokenCachePath(configuration);
        if (!File.Exists(legacyPath))
            return false;

        await CopyFileAtomicallyAsync(legacyPath, localPath, cancellationToken).ConfigureAwait(false);
        await EnsureFilesMatchAsync(localPath, legacyPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> PrepareGmailTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var localPath = AuthenticationTokenStorePaths.GetGmailTokenPath(configuration, accountId);
        if (await IsReusableGmailTokenAsync(localPath, cancellationToken).ConfigureAwait(false))
            return true;

        foreach (var legacyStorePath in AuthenticationTokenStorePaths.GetLegacyGmailTokenStorePaths(configuration))
        {
            var currentFormatPath = Path.Combine(legacyStorePath, $"{accountId:N}.json");
            var currentFormatToken = await TryReadCurrentTokenAsync(currentFormatPath, cancellationToken).ConfigureAwait(false);
            if (currentFormatToken?.HasReusableCredential == true)
            {
                await WriteTokenAtomicallyAsync(localPath, currentFormatToken, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var storeTokenPath = AuthenticationTokenStorePaths.GetLegacyGoogleTokenPath(legacyStorePath, accountId);
            var storeToken = await TryReadStoreTokenAsync(storeTokenPath, cancellationToken).ConfigureAwait(false);
            if (storeToken?.HasReusableCredential == true)
            {
                await WriteTokenAtomicallyAsync(localPath, storeToken, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private static async Task<LocalGoogleToken> ReadCurrentTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync(
                stream,
                AuthenticationTokenMigrationJsonContext.Default.LocalGoogleToken,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LocalGoogleToken> TryReadCurrentTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCurrentTokenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<LocalGoogleToken> ReadStoreTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            useAsync: true);
        var legacyToken = await JsonSerializer.DeserializeAsync(
                stream,
                AuthenticationTokenMigrationJsonContext.Default.LegacyGoogleToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (legacyToken is null)
            return null;

        var issuedAt = legacyToken.IssuedUtc ?? legacyToken.Issued ?? DateTimeOffset.UtcNow;
        return new LocalGoogleToken
        {
            AccessToken = legacyToken.AccessToken ?? string.Empty,
            RefreshToken = legacyToken.RefreshToken ?? string.Empty,
            ExpiresAtUtc = issuedAt.AddSeconds(Math.Max(legacyToken.ExpiresIn, 60)),
            Scopes = SplitScopes(legacyToken.Scope)
        };
    }

    private static async Task<LocalGoogleToken> TryReadStoreTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadStoreTokenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> IsReusableGmailTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await TryReadCurrentTokenAsync(path, cancellationToken).ConfigureAwait(false))
                ?.HasReusableCredential == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WriteTokenAtomicallyAsync(
        string destinationPath,
        LocalGoogleToken token,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The Gmail token destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        token,
                        AuthenticationTokenMigrationJsonContext.Default.LocalGoogleToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The Outlook token destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                81920,
                useAsync: true))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static async Task EnsureFilesMatchAsync(
        string destinationPath,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
            throw new InvalidDataException("The secured Outlook token cache was not created.");

        var destinationHash = await ComputeHashAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        var sourceHash = await ComputeHashAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(destinationHash, sourceHash))
            throw new InvalidDataException("The secured Outlook token cache did not match its source.");
    }

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            81920,
            useAsync: true);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> SplitScopes(string scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    internal sealed class LocalGoogleToken
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public List<string> Scopes { get; set; } = [];

        [JsonIgnore]
        public bool HasReusableCredential => !string.IsNullOrWhiteSpace(RefreshToken);
    }

    internal sealed class LegacyGoogleToken
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string Scope { get; set; }

        [JsonPropertyName("Issued")]
        public DateTimeOffset? Issued { get; set; }

        [JsonPropertyName("IssuedUtc")]
        public DateTimeOffset? IssuedUtc { get; set; }
    }
}

[JsonSerializable(typeof(AuthenticationTokenMigrationService.LocalGoogleToken))]
[JsonSerializable(typeof(AuthenticationTokenMigrationService.LegacyGoogleToken))]
internal partial class AuthenticationTokenMigrationJsonContext : JsonSerializerContext;
