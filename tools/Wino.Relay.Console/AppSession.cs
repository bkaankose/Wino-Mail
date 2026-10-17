using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
#if WINO_MACOS
using Wino.Platform.Apple;
using Wino.Platform.Apple.Security;
using Wino.Services.Security;
#endif

namespace Wino.Relay.ConsoleApp;

/// <summary>
/// The Wino Account the app on this machine is signed in to, read from the app's own database without writing it.
/// The console never refreshes the tokens: the API rotates refresh tokens, so a refresh from here would sign the app
/// out. When the access token has expired, open the app (it refreshes) and re-read.
/// </summary>
internal sealed class AppSession
{
    public const string DatabaseFileName = "Wino210.db";
    public const string MacBundleIdentifier = "com.winomail.macos";
    public const string WindowsPackageFamilyName = "58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj";

    private AppSession(string dataFolder, WinoAccount account)
    {
        DataFolder = dataFolder;
        Account = account;
    }

    public string DataFolder { get; }
    public WinoAccount Account { get; private set; }
    public bool IsAccessTokenValid => !string.IsNullOrWhiteSpace(Account.AccessToken) && Account.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1);
    public string AccessToken => Account.AccessToken;

    /// <summary>Candidate data folders of the app on this host, most likely first.</summary>
    public static IReadOnlyList<string> CandidateDataFolders(string? packageFamilyName)
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return
            [
                Path.Combine(home, "Library", "Containers", MacBundleIdentifier, "Data", "Library", "Application Support", MacBundleIdentifier),
                Path.Combine(home, "Library", "Application Support", MacBundleIdentifier),
            ];
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [Path.Combine(localAppData, "Packages", packageFamilyName ?? WindowsPackageFamilyName, "LocalState")];
    }

    public static async Task<AppSession?> OpenAsync(string dataFolder, CancellationToken cancellationToken)
    {
        var account = await ReadAccountAsync(dataFolder, cancellationToken);
        return account is null ? null : new AppSession(dataFolder, account);
    }

    /// <summary>Reads the account again, after the app refreshed its tokens.</summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        var account = await ReadAccountAsync(DataFolder, cancellationToken);
        if (account is null) return false;
        Account = account;
        return true;
    }

    private static async Task<WinoAccount?> ReadAccountAsync(string dataFolder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataFolder, DatabaseFileName);
        if (!File.Exists(path)) return null;

        WinoAccount? account;
        // Read-only: the app may be running, and this console must never change its session.
        using (var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex))
        {
            account = connection.Table<WinoAccount>().FirstOrDefault();
        }

        if (account is null) return null;
        await CredentialPersistence().RestoreWinoAccountSecretsAsync(account, cancellationToken);
        return account;
    }

    private static IAccountCredentialPersistence CredentialPersistence()
    {
#if WINO_MACOS
        // The Mac app keeps the tokens in the Keychain under com.winomail.macos.v1.*; macOS asks once whether this
        // console may read them.
        return new SecureItemAccountCredentialPersistence(new AppleKeychainStore(new AppleApplicationIdentity(MacBundleIdentifier)));
#else
        // Windows keeps the tokens in the database as they are.
        return new PassThroughCredentialPersistence();
#endif
    }

    private sealed class PassThroughCredentialPersistence : IAccountCredentialPersistence
    {
        public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Task.FromResult(information);
        public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default) => Task.FromResult(account);
        public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
