# Wino.Core.Domain

The contracts every other project builds on: service interfaces, entities, enums, models, and the translations. Every app and every shared project references it, so a change here rebuilds everything.

## Hard stops

- **Client ids** for Outlook, Gmail and Wino Account live in `Models/Authentication/WinoAuthenticationClientIds.cs`. The iOS notification extension links that file directly, so it stays dependency-free. The rules for the ids themselves are under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md).
- **An entity change is a schema change.** Entities are plain classes with sqlite-net attributes. Their tables are created in `CreateTablesAsync` and upgraded in `EnsureSchemaUpgradesAsync`, both in `src/Wino.Services/DatabaseService.cs`. Existing installs must keep their data: a new column gets an `ALTER TABLE … ADD COLUMN` step there. Ask the owner before a change that renames, drops or rewrites a column or a table.
- **Add no package reference here.** There is no CommunityToolkit in this project. A model that must raise change notifications derives from `ComponentModel/ObservableModel` and writes its properties by hand, in the generated order: compare, raise changing, assign, run the hook, raise changed, then dependent properties. There is no MimeKit or MailKit here either: a contract or model that needs a MIME or IMAP type lives in `src/Wino.Services/Domain` or `src/Wino.Core/Domain`, under its original `Wino.Core.Domain.*` namespace.
- **No UI state in new code.** Menu items, theme, navigation and printing models belong to `Wino.Mail.ViewModels`. The menu items still here remain only because `FolderService` in `Wino.Services` builds them and the shell contracts name them.

## Always

- Declare a contract in `Interfaces/` and implement it in `Wino.Services`, `Wino.Core` or a platform project.
- Write a contract as what the app needs, not how one platform does it: `Launch`, `Protect`, `Print`, never a window handle, a WebView type or a store path. Give external operations a `CancellationToken`, and let the result tell cancelled, unavailable and failed apart.
- Add English strings only to `Translations/en_US/resources.json`. The `Translator` class is generated from that file by `Wino.SourceGenerators` on every build. Other locales are managed elsewhere; leave them untouched.
- Keep `static` mutable state out. `Translator` and culture are the only process-wide state this project owns.

## Running a change

Build this project alone first; it targets plain `net10.0`:

```powershell
dotnet build src/Wino.Core.Domain/Wino.Core.Domain.csproj
```

A model or helper here can be called from a single-file script; see "Running backend code without an app" in the [root guidance](../../AGENTS.md). After a contract change, build every project that implements it, on each platform you can build.
