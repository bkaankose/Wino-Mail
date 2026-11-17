# Wino.Services

Storage and everything around it: the SQLite database and its schema, the MIME file store, mail rendering and sanitizing, preferences, logging, the Wino account client, and the portable implementations that Mac, iOS and Android share. It references `Wino.Core.Domain` and no platform or UI project.

## Hard stops

- **The database changes only after the server confirms.** A service here writes what a synchronizer's change processor reports after a successful network call. It never writes a guessed result, and there is no offline queue and no retry queue. The pipeline is described in [Wino.Core](../Wino.Core/AGENTS.md).
- **Existing installs keep their data.** The schema version is in `DatabaseService.cs` (`CurrentSchemaVersion`, file `Wino210.db`). Upgrades are inline steps in `EnsureSchemaUpgradesAsync`. Ask the owner before a change that renames, drops or rewrites existing data, or that changes a token, credential or settings format.
- **One log file.** Add no log file and no Serilog sink. Log when state changes, never on every timer tick or skipped item. A line that can repeat goes through `Logging/ThrottledLog.cs`.
- **No UI service in new code.** Do not inject a dialog, navigation or theme contract here. A service that needs a decision returns it to its caller.
- **Paths come from `IApplicationConfiguration`.** The database and EML files live under `ApplicationDataFolderPath`; resolve them with `MainDatabasePaths` and `MimeFileService.GetMimeMessagePath()`. The host fills the configuration before it resolves any service.

## Always

- Derive a database service from `BaseDatabaseService` and send UI messages with `ReportUIChange<TMessage>()`.
- Take dependencies through the constructor. New code takes `IMessenger` by injection and holds no `static` mutable state.
- Register the service in `RegisterSharedServices` (`ServicesContainerSetup.cs`). A class that implements several interfaces is registered once, and each interface forwards to that instance.
- Log errors with `IWinoLogger`, and wrap async external operations in try/catch.
- Keep JSON source-generated. Reflection-based serialization fails the Windows Release gate.

## Running a change

A storage service runs against a real database in a scratch folder. This script creates the full schema there; it ran in 4 seconds on a warm build:

```csharp
#:project D:/Wino-Mail/src/Wino.Services/Wino.Services.csproj
using Wino.Core.Domain.Entities.Shared;
using Wino.Services;

var root = args[0];   // an empty folder in your scratchpad
var configuration = new ApplicationConfiguration
{
    ApplicationDataFolderPath = root,
    PublisherSharedFolderPath = root,
    ApplicationTempFolderPath = Path.Combine(root, "temp"),
};

var database = new DatabaseService(configuration);
await database.InitializeAsync();

Console.WriteLine("schema version: " + await database.Connection.ExecuteScalarAsync<int>("PRAGMA user_version"));
Console.WriteLine("accounts: " + await database.Connection.Table<MailAccount>().CountAsync());
```

Construct the service you changed on top of `database` with its real dependencies, call it, and print what it stored. How to run a script, and the rules for it, are under "Running backend code without an app" in the [root guidance](../../AGENTS.md).

## Performance

- SQLite accepts a limited number of variables per statement. Split an `IN` list with `SqliteVariableLimit.Batch()`; the cap is 400 per query.
- The client log rolls daily and keeps three files. Log volume is a cost for every user who sends a log.
- `ThrottledLog` writes the first occurrence at once and then a count per window. It tracks at most 512 keys.
