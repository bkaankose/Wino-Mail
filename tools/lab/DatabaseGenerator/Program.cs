using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Users;
using Wino.Services;
using Wino.Services.Dav;
using Wino.Services.Security;

// Generates a LocalState folder for the lab: Wino210.db with one IMAP account per entry of `accounts`, the
// credentials in the app's secure item store (secure\), and an encrypted .winosnap backup. The input is the lab
// manifest or a lease file in the same shape. --pop3 also adds the `pop3Accounts` entries as POP3 accounts.
// --legacy-plaintext writes the pre-secure-store format instead (plaintext password columns and credentials\dav
// files) to test the upgrade migration.
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Run on Windows as the user who will run Wino (credentials use CurrentUser DPAPI).");

string[] switches = ["--legacy-plaintext", "--pop3"];
var legacyPlaintext = args.Contains("--legacy-plaintext", StringComparer.Ordinal);
var includePop3 = args.Contains("--pop3", StringComparer.Ordinal);
var positional = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length != 2 || args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) && !switches.Contains(argument)))
    throw new ArgumentException("Usage: DatabaseGenerator <generated-directory> <fixtures.json or lease.json> [--pop3] [--legacy-plaintext]");

var destination = Path.GetFullPath(positional[0]);
var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(positional[1]))!.AsObject();
var password = fixtures["password"]?.GetValue<string>() ?? "1";
var definitions = new List<LabAccount>();
foreach (var node in fixtures["accounts"]?.AsArray() ?? [])
    definitions.Add(LabAccount.FromImap(node!.AsObject(), definitions.Count, password));
if (includePop3)
{
    foreach (var node in fixtures["pop3Accounts"]?.AsArray() ?? [])
        definitions.Add(LabAccount.FromPop3(node!.AsObject(), definitions.Count, password));

    // The POP3 system folder names come from the English translations, as for an account created in the app.
    await using var english = Wino.Core.Domain.Translations.WinoTranslationDictionary.GetLanguageStream(AppLanguage.English);
    foreach (var (translationKey, value) in (await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(english))!)
        Translator.Resources[translationKey] = value;
}
if (definitions.Count == 0)
    throw new InvalidOperationException("The input contains no accounts.");
var duplicateKey = definitions.GroupBy(definition => definition.Key).FirstOrDefault(group => group.Count() > 1);
if (duplicateKey != null)
    throw new InvalidOperationException($"The account key '{duplicateKey.Key}' appears more than once; account ids derive from it.");

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
        var key = definition.Key;
        var id = StableId("account/" + key);
        var address = definition.Address;
        var displayName = definition.Name;
        var accountPassword = definition.Password;
        var signatureId = StableId("signature/" + key);
        var smtpAuth = definition.SmtpAuth;
        var hasDav = definition.DavUrl != null;
        var davUrl = definition.DavUrl ?? string.Empty;
        var cardDavUrl = definition.CardDavUrl ?? davUrl;
        var davUser = definition.DavUsername ?? string.Empty;
        var providerType = definition.IsPop3 ? MailProviderType.POP3 : MailProviderType.IMAP4;
        var calendarMode = hasDav ? ImapCalendarSupportMode.CalDav : ImapCalendarSupportMode.Disabled;
        var davSource = hasDav ? AccountIntegrationSource.Dav : AccountIntegrationSource.Provider;
        var maxClients = definition.IncomingHost.StartsWith("greenmail.", StringComparison.OrdinalIgnoreCase) ||
            definition.IncomingHost.StartsWith("james.", StringComparison.OrdinalIgnoreCase) ? 3 : 5;

        var account = new MailAccount
        {
            Id = id, Name = displayName, SenderName = displayName, Address = address,
            AuthenticationAddress = address, ProviderType = providerType,
            AccountColorHex = colors[index % colors.Length], Order = index, CreatedAt = DateTime.UtcNow,
            IsMailAccessGranted = true, IsCalendarAccessEnabled = hasDav, IsCalendarAccessGranted = hasDav,
            CalendarIntegrationSource = davSource,
            IsContactAccessEnabled = hasDav, IsContactAccessGranted = hasDav,
            ContactIntegrationSource = davSource,
            IsTaskAccessEnabled = false, InitialSynchronizationRange = InitialSynchronizationRange.Everything,
            ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending
        };
        var server = new CustomServerInformation
        {
            Id = StableId("server/" + key), AccountId = id, Address = address,
            IncomingServer = definition.IncomingHost, IncomingServerPort = definition.IncomingPort.ToString(),
            IncomingServerType = definition.IsPop3 ? CustomIncomingServerType.POP3 : CustomIncomingServerType.IMAP4,
            IncomingServerUsername = definition.IncomingUsername, IncomingServerPassword = accountPassword,
            IncomingServerSocketOption = definition.IncomingSecurity,
            IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
            OutgoingServer = definition.SmtpHost, OutgoingServerPort = definition.SmtpPort.ToString(),
            OutgoingServerUsername = smtpAuth == ImapAuthenticationMethod.None ? string.Empty : definition.SmtpUsername,
            OutgoingServerPassword = smtpAuth == ImapAuthenticationMethod.None ? string.Empty : accountPassword,
            OutgoingServerSocketOption = definition.SmtpSecurity,
            OutgoingAuthenticationMethod = smtpAuth,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected, MaxConcurrentClients = maxClients,
            CalendarSupportMode = calendarMode,
            CalDavServiceUrl = davUrl, CalDavUsername = davUser, CalDavPassword = hasDav ? accountPassword : string.Empty,
            CardDavServiceUrl = hasDav ? cardDavUrl : string.Empty,
            Pop3KeepOnServerWhenDeletedLocally = definition.IsPop3
        };
        var preferences = new MailAccountPreferences
        {
            Id = StableId("preferences/" + key), AccountId = id,
            ShouldAppendMessagesToSentFolder = !definition.IsPop3, IsNotificationsEnabled = true,
            IsSignatureEnabled = true, SignatureIdForNewMessages = signatureId,
            SignatureIdForFollowingMessages = signatureId
        };
        preferences.PrepareForStorage();
        var signatureHtml = $"<p>{displayName}<br>Wino lab ({definition.Server})</p>";

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
            if (hasDav)
                connection.Insert(new CardDavAccountState { AccountId = id, RequiresRediscovery = true });
            if (definition.IsPop3)
            {
                // Mirrors AccountService.EnsurePop3LocalFoldersAsync: a POP3 account owns local system folders only.
                foreach (var (remoteId, name, type) in Pop3Folders())
                {
                    connection.Insert(new MailItemFolder
                    {
                        Id = StableId($"folder/{key}/{remoteId}"), MailAccountId = id, RemoteFolderId = remoteId,
                        FolderName = name, SpecialFolderType = type, IsSystemFolder = true, IsSticky = true,
                        IsSynchronizationEnabled = type == SpecialFolderType.Inbox,
                        ShowUnreadCount = type != SpecialFolderType.Deleted
                    });
                }
            }
        });

        if (hasDav)
        {
            await credentials.SavePasswordAsync(id, accountPassword);
            if (await credentials.GetPasswordAsync(id) != accountPassword)
                throw new InvalidOperationException($"DAV credential decryption failed for {key}.");
        }
        await VerifyServerCredentialsAsync(database, secureRoot, protector, id, accountPassword, smtpAuth != ImapAuthenticationMethod.None, hasDav, legacyPlaintext, key);

        mailboxes.Add(new UserMailboxSyncItemDto
        {
            Address = address, ProviderType = (int)providerType, SpecialImapProvider = (int)account.SpecialImapProvider,
            AccountName = displayName, SenderName = displayName, AccountColorHex = account.AccountColorHex, SortOrder = index,
            IsCalendarAccessGranted = hasDav, CalendarSupportMode = (int)calendarMode,
            IncomingServer = server.IncomingServer, IncomingServerPort = server.IncomingServerPort, IncomingServerUsername = server.IncomingServerUsername,
            IncomingServerSocketOption = (int)server.IncomingServerSocketOption, IncomingAuthenticationMethod = (int)server.IncomingAuthenticationMethod,
            OutgoingServer = server.OutgoingServer, OutgoingServerPort = server.OutgoingServerPort, OutgoingServerUsername = server.OutgoingServerUsername,
            OutgoingServerSocketOption = (int)server.OutgoingServerSocketOption, OutgoingAuthenticationMethod = (int)server.OutgoingAuthenticationMethod,
            CalDavServiceUrl = davUrl, CalDavUsername = davUser, MaxConcurrentClients = maxClients, IsMailAccessGranted = true,
            ShouldAppendMessagesToSentFolder = preferences.ShouldAppendMessagesToSentFolder, IsNotificationsEnabled = true, IsSignatureEnabled = true,
            SignatureIdForNewMessages = signatureId, SignatureIdForFollowingMessages = signatureId,
            Signatures = [new UserMailboxSignatureSyncItemDto { Id = signatureId, Name = "Lab signature", HtmlBody = signatureHtml }],
            Folders = []
        });
        capabilitiesSnapshot.Add(new
        {
            AccountAddress = address, ProviderType = (int)providerType,
            IsCalendarEnabled = hasDav, CalendarIntegrationSource = (int)davSource,
            IsContactsEnabled = hasDav, ContactIntegrationSource = (int)davSource,
            CardDavServiceUrl = server.CardDavServiceUrl, IsTasksEnabled = false, TaskIntegrationSource = 0
        });

        accounts.Add(new
        {
            id, key, name = displayName, address, provider = providerType.ToString(), davUsername = davUser,
            incoming = $"{server.IncomingServer}:{server.IncomingServerPort}", smtp = $"{server.OutgoingServer}:{server.OutgoingServerPort}",
            smtpAuthentication = smtpAuth.ToString(), caldav = davUrl, carddav = server.CardDavServiceUrl,
            server = definition.Server, expectedStrategy = definition.Source["expectedStrategy"]?.GetValue<string>(),
            capabilities = definition.Source["capabilities"]?.AsArray().Select(c => c!.GetValue<string>()).ToArray()
        });
    }

    var expected = definitions.Count;
    var expectedDav = definitions.Count(definition => definition.DavUrl != null);
    var expectedFolders = definitions.Count(definition => definition.IsPop3) * Pop3Folders().Length;
    if (await database.Connection.Table<MailAccount>().CountAsync() != expected ||
        await database.Connection.Table<CustomServerInformation>().CountAsync() != expected ||
        await database.Connection.Table<MailAccountPreferences>().CountAsync() != expected ||
        await database.Connection.Table<AccountSignature>().CountAsync() != expected ||
        await database.Connection.Table<MailAccountAlias>().CountAsync() != expected ||
        await database.Connection.Table<CardDavAccountState>().CountAsync() != expectedDav ||
        await database.Connection.Table<MailItemFolder>().CountAsync() != expectedFolders ||
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
    // mirrors WinoSyncSnapshotDocument; passwords are never part of a snapshot, so restoring asks for the lab password.
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
        $"{expected} accounts, credentials in the {credentialFormat} (verified), {backupName} (the lab password opens it); no cached mail/events/contacts.");
}
finally
{
    if (!closed) await database.Connection.CloseAsync();
    if (Directory.Exists(staging)) await RetryFileOperationAsync(() => Directory.Delete(staging, true));
}

// Reads the row back and restores it with a fresh persistence (no cache), as the app does after a restart.
static async Task VerifyServerCredentialsAsync(DatabaseService database, string secureRoot, Wino.Platform.Windows.DpapiSecretProtector protector,
    Guid accountId, string password, bool hasSmtpPassword, bool hasDav, bool legacyPlaintext, string key)
{
    var stored = await database.Connection.Table<CustomServerInformation>().FirstOrDefaultAsync(row => row.AccountId == accountId)
        ?? throw new InvalidOperationException($"The server information of {key} was not stored.");
    var isReference = stored.IncomingServerPassword?.StartsWith("wino-keychain:v1:", StringComparison.Ordinal) == true;
    if (isReference == legacyPlaintext)
        throw new InvalidOperationException($"The stored password of {key} is not in the requested format.");

    var restorer = ProtectedFileCredentialStorage.CreateLegacyAwarePersistence(new ProtectedFileSecureItemStore(secureRoot, protector));
    await restorer.RestoreServerInformationSecretsAsync(stored);
    if (stored.IncomingServerPassword != password || stored.CalDavPassword != (hasDav ? password : string.Empty) ||
        stored.OutgoingServerPassword != (hasSmtpPassword ? password : string.Empty))
        throw new InvalidOperationException($"The stored passwords of {key} did not restore.");
}

static (string RemoteId, string Name, SpecialFolderType Type)[] Pop3Folders() =>
[
    ("local-inbox", Translator.POP3Folder_Inbox, SpecialFolderType.Inbox),
    ("local-drafts", Translator.POP3Folder_Drafts, SpecialFolderType.Draft),
    ("local-sent", Translator.POP3Folder_Sent, SpecialFolderType.Sent),
    ("local-archive", Translator.POP3Folder_Archive, SpecialFolderType.Archive),
    ("local-deleted", Translator.POP3Folder_Deleted, SpecialFolderType.Deleted)
];

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

sealed record LabAccount(
    JsonObject Source, string Key, string Server, string Address, string Name, string Password, bool IsPop3,
    string IncomingHost, int IncomingPort, ImapConnectionSecurity IncomingSecurity, string IncomingUsername,
    string SmtpHost, int SmtpPort, ImapConnectionSecurity SmtpSecurity, ImapAuthenticationMethod SmtpAuth, string SmtpUsername,
    string? DavUrl, string? CardDavUrl, string? DavUsername)
{
    public static LabAccount FromImap(JsonObject definition, int index, string password)
    {
        var reader = new Reader(definition, index);
        var key = reader.Text("key");
        var address = reader.Text("address");
        var davUrl = reader.Optional("davUrl");
        return new LabAccount(definition, key, reader.Optional("server") ?? key, address, reader.Text("name"),
            reader.Optional("password") ?? password, false,
            reader.Text("imapHost"), reader.Number("imapPort"), reader.Security("imapSecurity"), address,
            reader.Text("smtpHost"), reader.Number("smtpPort"), reader.Security("smtpSecurity"), reader.Authentication("smtpAuth"),
            reader.Optional("smtpUsername") ?? address,
            davUrl, davUrl == null ? null : reader.Optional("cardDavUrl"), davUrl == null ? null : reader.Text("davUsername"));
    }

    public static LabAccount FromPop3(JsonObject definition, int index, string password)
    {
        var reader = new Reader(definition, index);
        var key = reader.Text("key");
        var address = reader.Text("address");
        var username = reader.Optional("username") ?? address;
        return new LabAccount(definition, key, reader.Optional("server") ?? key, address, reader.Text("name"),
            reader.Optional("password") ?? password, true,
            reader.Text("pop3Host"), reader.Number("pop3Port"), reader.Security("pop3Security"), username,
            reader.Text("smtpHost"), reader.Number("smtpPort"), reader.Security("smtpSecurity"), reader.Authentication("smtpAuth"),
            reader.Optional("smtpUsername") ?? username, null, null, null);
    }

    private readonly record struct Reader(JsonObject Definition, int Index)
    {
        public string? Optional(string name) => Definition[name]?.GetValue<string>() is { Length: > 0 } value ? value : null;
        public string Text(string name) => Optional(name) ?? throw new InvalidOperationException($"Account {Index} lacks '{name}'.");
        public int Number(string name) => Definition[name]?.GetValue<int>() ?? throw new InvalidOperationException($"Account {Index} lacks '{name}'.");
        public ImapConnectionSecurity Security(string name) => Enum.Parse<ImapConnectionSecurity>(Optional(name) ?? "None");
        public ImapAuthenticationMethod Authentication(string name) => Enum.Parse<ImapAuthenticationMethod>(Optional(name) ?? "NormalPassword");
    }
}
