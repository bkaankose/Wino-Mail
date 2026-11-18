# Wino.Providers.Dav

Calendars, tasks and contacts over CalDAV and CardDAV: the three synchronization engines, the endpoint cache, and the CardDAV address book and contact list services. Accounts of any mail provider can use them, so DAV is not a mail provider type: `AddDavProvider` registers services and no provider module. It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline.

## Hard stops

- **Follow the RFC and stay generic** (RFC 4791 for CalDAV, RFC 6352 for CardDAV, RFC 6764 for discovery). A fix applies to every conforming server. Add no per-server exception; for a server that misbehaves, write a note for the owner with the request and the reply.
- **The database changes only after the server confirms.** A CalDAV move is a PUT followed by a DELETE. When an operation needs several calls, say what state a failure between them leaves on the server, and make the failure visible to the user.
- **Do not break data that existing users have already synced**, and make no schema change for a synchronizer fix.
- **No other provider.** This project never references another `Wino.Providers.*` project. Other providers reach these engines through the engine contracts in `Wino.Core.Domain`.

## Always

- Register services in `AddDavProvider` ([Providers/DavProvider.cs](Providers/DavProvider.cs)).
- The calendar store is `CalDavChangeProcessor` in `Wino.Core`, with `CalendarIcsBuilder` and `CalDavAttachmentRows`. They stay there because IMAP accounts and local calendars use them too. Change calendar storage there, not here.
- Discovered endpoints are cached in `DavEndpointCache`, a container singleton. Add no static cache.
- DAV credentials are stored through `IDavCredentialStore`. Never write a password to a log or a file.
- Every file's namespace is `Wino.Providers.Dav` plus its folder path.

## Running a change

Run every change against the mail lab from a script through [Wino.Hosting](../Wino.Hosting/AGENTS.md): add a leased lab account whose server offers CalDAV or CardDAV, synchronize, then create, change and delete an item and read it back from the server. [tools/lab/README.md](../../tools/lab/README.md) says which lab server covers which behavior.

## Performance

| Limit | Value | Where |
| --- | --- | --- |
| Item download | 50 per batch, 4 batches at once | `CalDavSynchronizationEngine.cs`, `CardDavSynchronizationEngine.cs` |

Use the server's sync token or ctag when it offers one. A full listing of a large calendar is the slow path.
