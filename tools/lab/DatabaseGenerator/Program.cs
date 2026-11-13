using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Users;
using Wino.Services;
using Wino.Services.Dav;
using Wino.Services.Security;

// Generates a LocalState folder for the lab: Wino210.db with one IMAP account per lab server, the credentials in
// the app's secure item store (secure\), and an encrypted .winosnap backup. Account definitions come from the
// seed tool's fixtures.json. --legacy-plaintext writes the pre-secure-store format instead (plaintext password
// columns and credentials\dav files) to test the upgrade migration.
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Run on Windows as the user who will run Wino (credentials use CurrentUser DPAPI).");

var legacyPlaintext = args.Contains("--legacy-plaintext", StringComparer.Ordinal);
var positional = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length != 2 || args.Length - positional.Length != (legacyPlaintext ? 1 : 0))
    throw new ArgumentException("Usage: DatabaseGenerator <generated-directory> <fixtures.json> [--legacy-plaintext]");

var destination = Path.GetFullPath(positional[0]);
var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(positional[1]))!.AsObject();
var password = fixtures["password"]?.GetValue<string>() ?? "1";
var definitions = fixtures["accounts"]!.AsArray().Select(a => a!.AsObject()).ToList();
if (definitions.Count == 0)
    throw new InvalidOperationException("fixtures.json contains no accounts. Run the seed tool first.");

var parent = Path.GetDirectoryName(destination)!;
Directory.CreateDirectory(parent);
var staging = Path.Combine(parent, "generated-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
var configuration = new ApplicationConfiguration
{
    ApplicationDataFolderPath = staging,
    PublisherSharedFolderPath = staging,
    ApplicationTempFolderPath = staging
};
var database = new DatabaseService(configuration);
var closed = false;
var colors = new[] { "#2675BF", "#B05A32", "#577B49", "#7A4B9E", "#B8860B" };

try
{
    await database.InitializeAsync();
    var protector = new Wino.Platform.Windows.DpapiSecretProtector();
    var secureRoot = ProtectedFileCredentialStorage.GetSecureItemRoot(configuration);
    // The same store and persistence the Windows app registers (ProtectedFileCredentialStorage).
    var persistence = ProtectedFileCredentialStorage.CreateLegacyAwarePersistence(new ProtectedFileSecureItemStore(secureRoot, protector));
    Wino.Core.Domain.Interfaces.IDavCredentialStore credentials = legacyPlaintext
        ? new DavCredentialStore(configuration, protector)
        : new SecureItemDavCredentialStore(new ProtectedFileSecureItemStore(secureRoot, protector));
    var accounts = new List<object>();
    var mailboxes = new List<UserMailboxSyncItemDto>();
    var capabilitiesSnapshot = new List<object>();

    for (var index = 0; index < definitions.Count; index++)
    {
        var definition = definitions[index];
        string Text(string name) => definition[name]?.GetValue<string>() ?? throw new InvalidOperationException($"fixtures.json account {index} lacks '{name}'.");
        int Number(string name) => definition[name]!.GetValue<int>();

        var key = Text("key");
        var id = StableId("account/" + key);
        var address = Text("address");
        var displayName = Text("name");
        var signatureId = StableId("signature/" + key);
        var smtpAuth = Enum.Parse<ImapAuthenticationMethod>(definition["smtpAuth"]?.GetValue<string>() ?? "NormalPassword");
        var imapSecurity = Enum.Parse<ImapConnectionSecurity>(definition["imapSecurity"]?.GetValue<string>() ?? "None");
        var smtpSecurity = Enum.Parse<ImapConnectionSecurity>(definition["smtpSecurity"]?.GetValue<string>() ?? "None");
        var smtpUsername = definition["smtpUsername"]?.GetValue<string>() ?? address;
        var davUrl = Text("davUrl");
        var cardDavUrl = definition["cardDavUrl"]?.GetValue<string>() ?? davUrl;
        var davUser = Text("davUsername");
        var maxClients = key is "greenmail" or "james" ? 3 : 5;

        var account = new MailAccount
        {
            Id = id, Name = displayName, SenderName = displayName, Address = address,
            AuthenticationAddress = address, ProviderType = MailProviderType.IMAP4,
            AccountColorHex = colors[index % colors.Length], Order = index, CreatedAt = DateTime.UtcNow,
            IsMailAccessGranted = true, IsCalendarAccessEnabled = true, IsCalendarAccessGranted = true,
            CalendarIntegrationSource = AccountIntegrationSource.Dav,
            IsContactAccessEnabled = true, IsContactAccessGranted = true,
            ContactIntegrationSource = AccountIntegrationSource.Dav,
            IsTaskAccessEnabled = false, InitialSynchronizationRange = InitialSynchronizationRange.Everything,
            ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending
        };
        var server = new CustomServerInformation
        {
            Id = StableId("server/" + key), AccountId = id, Address = address,
            IncomingServer = Text("imapHost"), IncomingServerPort = Number("imapPort").ToString(),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingServerUsername = address, IncomingServerPassword = password,
            IncomingServerSocketOption = imapSecurity,
            IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
            OutgoingServer = Text("smtpHost"), OutgoingServerPort = Number("smtpPort").ToString(),
            OutgoingServerUsername = smtpAuth == ImapAuthenticationMethod.None ? string.Empty : smtpUsername,
            OutgoingServerPassword = smtpAuth == ImapAuthenticationMethod.None ? string.Empty : password,
            OutgoingServerSocketOption = smtpSecurity,
            OutgoingAuthenticationMethod = smtpAuth,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected, MaxConcurrentClients = maxClients,
            CalendarSupportMode = ImapCalendarSupportMode.CalDav,
            CalDavServiceUrl = davUrl, CalDavUsername = davUser, CalDavPassword = password,
            CardDavServiceUrl = cardDavUrl
        };
        var preferences = new MailAccountPreferences
        {
            Id = StableId("preferences/" + key), AccountId = id,
            ShouldAppendMessagesToSentFolder = true, IsNotificationsEnabled = true,
            IsSignatureEnabled = true, SignatureIdForNewMessages = signatureId,
            SignatureIdForFollowingMessages = signatureId
        };
        preferences.PrepareForStorage();
        var signatureHtml = $"<p>{displayName}<br>Wino lab ({definition["server"]?.GetValue<string>()})</p>";

        var storedServer = legacyPlaintext ? server : await persistence.PrepareServerInformationForStorageAsync(server);
        await database.Connection.RunInTransactionAsync(connection =>
        {
            connection.Insert(account);
            connection.Insert(storedServer);
            connection.Insert(preferences);
            connection.Insert(new AccountSignature { Id = signatureId, MailAccountId = id, Name = "Lab signature", HtmlBody = signatureHtml });
            connection.Insert(new MailAccountAlias
            {
                Id = StableId("alias/" + key), AccountId = id, AliasAddress = address,
                AliasSenderName = displayName, ReplyToAddress = address,
                IsRootAlias = true, IsPrimary = true, IsVerified = true,
                Source = AliasSource.Manual, SendCapability = AliasSendCapability.Confirmed
            });
            connection.Insert(new CardDavAccountState { AccountId = id, RequiresRediscovery = true });
        });

        await credentials.SavePasswordAsync(id, password);
        if (await credentials.GetPasswordAsync(id) != password)
            throw new InvalidOperationException($"DAV credential decryption failed for {key}.");
        await VerifyServerCredentialsAsync(database, secureRoot, protector, id, password, smtpAuth != ImapAuthenticationMethod.None, legacyPlaintext, key);

        mailboxes.Add(new UserMailboxSyncItemDto
        {
            Address = address, ProviderType = (int)MailProviderType.IMAP4, SpecialImapProvider = (int)account.SpecialImapProvider,
            AccountName = displayName, SenderName = displayName, AccountColorHex = account.AccountColorHex, SortOrder = index,
            IsCalendarAccessGranted = true, CalendarSupportMode = (int)ImapCalendarSupportMode.CalDav,
            IncomingServer = server.IncomingServer, IncomingServerPort = server.IncomingServerPort, IncomingServerUsername = server.IncomingServerUsername,
            IncomingServerSocketOption = (int)server.IncomingServerSocketOption, IncomingAuthenticationMethod = (int)server.IncomingAuthenticationMethod,
            OutgoingServer = server.OutgoingServer, OutgoingServerPort = server.OutgoingServerPort, OutgoingServerUsername = server.OutgoingServerUsername,
            OutgoingServerSocketOption = (int)server.OutgoingServerSocketOption, OutgoingAuthenticationMethod = (int)server.OutgoingAuthenticationMethod,
            CalDavServiceUrl = davUrl, CalDavUsername = davUser, MaxConcurrentClients = maxClients, IsMailAccessGranted = true,
            ShouldAppendMessagesToSentFolder = true, IsNotificationsEnabled = true, IsSignatureEnabled = true,
            SignatureIdForNewMessages = signatureId, SignatureIdForFollowingMessages = signatureId,
            Signatures = [new UserMailboxSignatureSyncItemDto { Id = signatureId, Name = "Lab signature", HtmlBody = signatureHtml }],
            Folders = []
        });
        capabilitiesSnapshot.Add(new
        {
            AccountAddress = address, ProviderType = (int)MailProviderType.IMAP4,
            IsCalendarEnabled = true, CalendarIntegrationSource = (int)AccountIntegrationSource.Dav,
            IsContactsEnabled = true, ContactIntegrationSource = (int)AccountIntegrationSource.Dav,
            CardDavServiceUrl = cardDavUrl, IsTasksEnabled = false, TaskIntegrationSource = 0
        });

        accounts.Add(new
        {
            id, key, name = displayName, address, password, davUsername = davUser,
            imap = $"{server.IncomingServer}:{server.IncomingServerPort}", smtp = $"{server.OutgoingServer}:{server.OutgoingServerPort}",
            smtpAuthentication = smtpAuth.ToString(), caldav = davUrl, carddav = cardDavUrl,
            server = definition["server"]?.GetValue<string>(), expectedStrategy = definition["expectedStrategy"]?.GetValue<string>(),
            capabilities = definition["capabilities"]?.AsArray().Select(c => c!.GetValue<string>()).ToArray()
        });
    }

    var expected = definitions.Count;
    if (await database.Connection.Table<MailAccount>().CountAsync() != expected ||
        await database.Connection.Table<CustomServerInformation>().CountAsync() != expected ||
        await database.Connection.Table<MailAccountPreferences>().CountAsync() != expected ||
        await database.Connection.Table<AccountSignature>().CountAsync() != expected ||
        await database.Connection.Table<MailAccountAlias>().CountAsync() != expected ||
        await database.Connection.Table<CardDavAccountState>().CountAsync() != expected ||
        await database.Connection.Table<MailCopy>().CountAsync() != 0 ||
        await database.Connection.Table<CalendarItem>().CountAsync() != 0 ||
        await database.Connection.Table<AccountContact>().CountAsync() != 0)
        throw new InvalidOperationException("Generated account configuration failed its row-count checks.");

    var checkpointBusy = await database.Connection.ExecuteScalarAsync<int>("PRAGMA wal_checkpoint(TRUNCATE);");
    if (checkpointBusy != 0)
        throw new InvalidOperationException("SQLite checkpoint is busy; refusing to publish an incomplete database.");
    await database.Connection.CloseAsync();
    closed = true;
    var validation = await new DatabaseSchemaService(configuration).ValidateAsync(
        Path.Combine(staging, DatabaseService.CurrentDatabaseName), requireCompletedMigration: true);
    if (!validation.IsValid)
        throw new InvalidOperationException($"Database validation failed: {validation}");

    // Encrypted backup in Wino's own snapshot format (Settings > Backup & restore). The document shape
    // mirrors WinoSyncSnapshotDocument; passwords are never part of a snapshot, so restoring asks for "1".
    var snapshot = new
    {
        Version = 1, ExportedAtUtc = DateTime.UtcNow, Mailboxes = mailboxes, AccountCapabilities = capabilitiesSnapshot
    };
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    var keyService = new SyncSnapshotKeyService(configuration);
    var snapshotKey = await keyService.DeriveAsync(password, keyService.CreateParameters());
    var sealedSnapshot = SyncSnapshotCryptography.Encrypt(plaintext, snapshotKey);
    var header = SyncSnapshotCryptography.ReadHeader(sealedSnapshot);
    var reopened = await keyService.DeriveAsync(password, header.ToKeyParameters());
    var decrypted = SyncSnapshotCryptography.Decrypt(sealedSnapshot, reopened.Key);
    if (!decrypted.AsSpan().SequenceEqual(plaintext))
        throw new InvalidOperationException("The generated backup did not round-trip with the lab password.");
    var backupName = "wino-lab-backup.winosnap";
    await File.WriteAllBytesAsync(Path.Combine(staging, backupName), sealedSnapshot);

    var manifest = new
    {
        schemaVersion = validation.SchemaVersion, generatedAtUtc = DateTime.UtcNow,
        windowsUser = Environment.UserDomainName + "\\" + Environment.UserName,
        machine = Environment.MachineName, database = DatabaseService.CurrentDatabaseName,
        credentialFormat = legacyPlaintext ? "legacy-plaintext" : "secure",
        backup = backupName, backupPassword = password, anchorDate = fixtures["anchorDate"]?.GetValue<string>(),
        accounts
    };
    await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    var backup = Path.Combine(parent, "generated-backup-" + Guid.NewGuid().ToString("N"));
    if (Directory.Exists(destination))
        await RetryFileOperationAsync(() => Directory.Move(destination, backup));
    try { await RetryFileOperationAsync(() => Directory.Move(staging, destination)); }
    catch
    {
        if (Directory.Exists(backup)) await RetryFileOperationAsync(() => Directory.Move(backup, destination));
        throw;
    }
    if (Directory.Exists(backup))
    {
        try { await RetryFileOperationAsync(() => Directory.Delete(backup, true)); }
        catch (IOException) { Console.WriteLine($"Previous artifacts remain in {backup}; the new artifacts are complete."); }
        catch (UnauthorizedAccessException) { Console.WriteLine($"Previous artifacts remain in {backup}; the new artifacts are complete."); }
    }
    var credentialFormat = legacyPlaintext ? @"legacy plaintext columns and credentials\dav files" : "secure item store";
    Console.WriteLine($"Generated {destination}: schema {validation.SchemaVersion}, integrity {validation.IntegrityResult}, " +
        $"{expected} accounts, credentials in the {credentialFormat} (verified), {backupName} (password {password}); no cached mail/events/contacts.");
}
finally
{
    if (!closed) await database.Connection.CloseAsync();
    if (Directory.Exists(staging)) await RetryFileOperationAsync(() => Directory.Delete(staging, true));
}

// Reads the row back and restores it with a fresh persistence (no cache), as the app does after a restart.
static async Task VerifyServerCredentialsAsync(DatabaseService database, string secureRoot, Wino.Platform.Windows.DpapiSecretProtector protector,
    Guid accountId, string password, bool hasSmtpPassword, bool legacyPlaintext, string key)
{
    var stored = await database.Connection.Table<CustomServerInformation>().FirstOrDefaultAsync(row => row.AccountId == accountId)
        ?? throw new InvalidOperationException($"The server information of {key} was not stored.");
    var isReference = stored.IncomingServerPassword?.StartsWith("wino-keychain:v1:", StringComparison.Ordinal) == true;
    if (isReference == legacyPlaintext)
        throw new InvalidOperationException($"The stored password of {key} is not in the requested format.");

    var restorer = ProtectedFileCredentialStorage.CreateLegacyAwarePersistence(new ProtectedFileSecureItemStore(secureRoot, protector));
    await restorer.RestoreServerInformationSecretsAsync(stored);
    if (stored.IncomingServerPassword != password || stored.CalDavPassword != password ||
        stored.OutgoingServerPassword != (hasSmtpPassword ? password : string.Empty))
        throw new InvalidOperationException($"The stored passwords of {key} did not restore.");
}

static Guid StableId(string key)
    => new(SHA256.HashData(Encoding.UTF8.GetBytes("Wino.LocalLab/" + key)).AsSpan(0, 16));

static async Task RetryFileOperationAsync(Action operation)
{
    for (var attempt = 0; ; attempt++)
    {
        try { operation(); return; }
        catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(100); }
    }
}
