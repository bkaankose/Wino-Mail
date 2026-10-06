using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Wino.Services.Dav;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Run on Windows as the user who will run Wino (DAV credentials use CurrentUser DPAPI).");

if (args.Length != 2)
    throw new ArgumentException("Usage: DatabaseGenerator <generated-directory> <fixtures.json>");

const string password = "WinoLab123!";
var destination = Path.GetFullPath(args[0]);
var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
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

try
{
    await database.InitializeAsync();
    var credentials = new DavCredentialStore(configuration, new Wino.Platform.Windows.DpapiSecretProtector());
    var accounts = new List<object>();
    var users = new[] { "alice", "bob", "empty" };
    var colors = new[] { "#2675BF", "#B05A32", "#577B49" };

    for (var index = 0; index < users.Length; index++)
    {
        var user = users[index];
        var id = StableId("account/" + user);
        var address = user + "@wino.test";
        var displayName = char.ToUpperInvariant(user[0]) + user[1..] + " Lab";
        var signatureId = StableId("signature/" + user);
        var account = new MailAccount
        {
            Id = id, Name = displayName, SenderName = displayName, Address = address,
            AuthenticationAddress = address, ProviderType = MailProviderType.IMAP4,
            AccountColorHex = colors[index], Order = index, CreatedAt = DateTime.UtcNow,
            IsMailAccessGranted = true, IsCalendarAccessEnabled = true, IsCalendarAccessGranted = true,
            CalendarIntegrationSource = AccountIntegrationSource.Dav,
            IsContactAccessEnabled = true, IsContactAccessGranted = true,
            ContactIntegrationSource = AccountIntegrationSource.Dav,
            IsTaskAccessEnabled = false, InitialSynchronizationRange = InitialSynchronizationRange.Everything,
            ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending
        };
        var server = new CustomServerInformation
        {
            Id = StableId("server/" + user), AccountId = id, Address = address,
            IncomingServer = "127.0.0.1", IncomingServerPort = "1143",
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingServerUsername = address, IncomingServerPassword = password,
            IncomingServerSocketOption = ImapConnectionSecurity.None,
            IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
            OutgoingServer = "127.0.0.1", OutgoingServerPort = "1587",
            OutgoingServerUsername = address, OutgoingServerPassword = password,
            OutgoingServerSocketOption = ImapConnectionSecurity.None,
            OutgoingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected, MaxConcurrentClients = 5,
            CalendarSupportMode = ImapCalendarSupportMode.CalDav,
            CalDavServiceUrl = "http://127.0.0.1:8800/dav.php/", CalDavUsername = user, CalDavPassword = password,
            CardDavServiceUrl = "http://127.0.0.1:8800/dav.php/"
        };
        var preferences = new MailAccountPreferences
        {
            Id = StableId("preferences/" + user), AccountId = id,
            ShouldAppendMessagesToSentFolder = true, IsNotificationsEnabled = true,
            IsSignatureEnabled = true, SignatureIdForNewMessages = signatureId,
            SignatureIdForFollowingMessages = signatureId
        };
        preferences.PrepareForStorage();

        // Use current entity mappings and schema, not a separate SQL schema snapshot.
        await database.Connection.RunInTransactionAsync(connection =>
        {
            connection.Insert(account);
            connection.Insert(server);
            connection.Insert(preferences);
            connection.Insert(new AccountSignature
            {
                Id = signatureId, MailAccountId = id, Name = "Local lab signature",
                HtmlBody = $"<p>{displayName}<br>Wino local lab</p>"
            });
            connection.Insert(new MailAccountAlias
            {
                Id = StableId("alias/" + user), AccountId = id, AliasAddress = address,
                AliasSenderName = displayName, ReplyToAddress = address,
                IsRootAlias = true, IsPrimary = true, IsVerified = true,
                Source = AliasSource.Manual, SendCapability = AliasSendCapability.Confirmed
            });
            connection.Insert(new CardDavAccountState { AccountId = id, RequiresRediscovery = true });
        });

        await credentials.SavePasswordAsync(id, password);
        if (await credentials.GetPasswordAsync(id) != password)
            throw new InvalidOperationException($"DAV credential decryption failed for {user}.");

        accounts.Add(new { id, name = displayName, address, password, davUsername = user,
            calendarHome = $"http://127.0.0.1:8800/dav.php/calendars/{user}/",
            addressBookHome = $"http://127.0.0.1:8800/dav.php/addressbooks/{user}/" });
    }

    if (await database.Connection.Table<MailAccount>().CountAsync() != 3 ||
        await database.Connection.Table<CustomServerInformation>().CountAsync() != 3 ||
        await database.Connection.Table<MailAccountPreferences>().CountAsync() != 3 ||
        await database.Connection.Table<AccountSignature>().CountAsync() != 3 ||
        await database.Connection.Table<MailAccountAlias>().CountAsync() != 3 ||
        await database.Connection.Table<CardDavAccountState>().CountAsync() != 3 ||
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

    var manifest = new
    {
        schemaVersion = validation.SchemaVersion, generatedAtUtc = DateTime.UtcNow,
        windowsUser = Environment.UserDomainName + "\\" + Environment.UserName,
        machine = Environment.MachineName, database = DatabaseService.CurrentDatabaseName,
        imap = new { host = "127.0.0.1", port = 1143, security = "None" },
        smtp = new { host = "127.0.0.1", port = 1587, security = "None" },
        dav = "http://127.0.0.1:8800/dav.php/", accounts,
        catalog = fixtures.RootElement.Clone()
    };
    await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    // Swap a complete artifact directory; preserve the old output if publication fails.
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
    Console.WriteLine($"Generated {destination}: schema {validation.SchemaVersion}, integrity {validation.IntegrityResult}, " +
        "3 accounts, 3 decrypted DAV credentials; no cached mail/events/contacts.");
}
finally
{
    fixtures.Dispose();
    if (!closed) await database.Connection.CloseAsync();
    if (Directory.Exists(staging)) await RetryFileOperationAsync(() => Directory.Delete(staging, true));
}

static Guid StableId(string key)
    => new(SHA256.HashData(Encoding.UTF8.GetBytes("Wino.LocalLab/" + key)).AsSpan(0, 16));

static async Task RetryFileOperationAsync(Action operation)
{
    // Windows file scanners can briefly hold a newly written database directory.
    for (var attempt = 0; ; attempt++)
    {
        try { operation(); return; }
        catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(100); }
    }
}
