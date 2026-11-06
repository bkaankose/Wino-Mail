# Exchange ActiveSync

The Windows account wizard includes an Exchange ActiveSync provider. It uses an explicit HTTPS endpoint and HTTP Basic authentication with a username and password. OAuth, SMTP and Microsoft Graph are not used by this provider. Credentials use the existing protected account credential store.

The client negotiates EAS 14.1, 14.0, 12.1 or 12.0. EAS 12.0 includes the collection Class in Sync requests; both 12.x versions send raw MIME through SendMail. A server advertising a newer version can use the provider if it also advertises one of these versions. Server compatibility must be established through interoperability testing; a shared protocol name does not establish compatibility with every Exchange, Zimbra, SOGo, Zoho or Z-Push deployment.

## Integration

- `Wino.Core/Synchronizers/Eas` owns WBXML, HTTPS, provisioning, synchronization and the provider adapter.
- `EasCollectionState` stores folder metadata and collection cursors. `EasItemShadow` retains provider properties that the domain models cannot represent. Account deletion removes both. Mail cache resets invalidate mail collection cursors. Restoring an account without these tables starts a fresh folder synchronization.
- The existing mail, calendar, contact and task services persist the projected data and notify the UI. Account setup and server settings use `IEasConnectionService` to check the endpoint before storing credentials.
- Mail uses `FolderSync`, `Sync`, `ItemOperations`, `MoveItems` and `SendMail`. Calendar, contacts and tasks use their EAS collections. Task list and mail folder changes use the folder commands.
- Remote mutations are serialized per account. Send requests have stable client identifiers on 14.x and are never automatically retried after an uncertain network result.
- Calendar creation commits the client identity before projecting server echoes. Calendar UIDs and the existing client tracking mechanism reconcile events created by Wino when the server returns a different EAS ID. Previously duplicated local projections are reconciled through a calendar re-sync; no server deletion is sent for this repair.

## Boundaries

This is an initial implementation pending complete interoperability verification. Initial synchronization and calendar identity reconciliation have been checked with Zoho EAS 12.0 in the separate Windows test package.

- Enter the final endpoint, typically `https://mail.example.com/Microsoft-Server-ActiveSync`. Autodiscover and automatic redirects are not implemented. TLS certificate validation remains enabled.
- Servers requiring OAuth, client certificates, NTLM or unsupported device policies are rejected. Provisioning acknowledges only permissive policies the app can satisfy. Remote wipe requests are rejected without deleting local data.
- Synchronization uses Wino's polling scheduler. EAS `Ping` push is not implemented.
- Provider integration requires the server to return the relevant collection in FolderSync. A missing Tasks collection is reported as unavailable rather than a successful empty task synchronization. Existing local task lists remain local.
- EAS 12.x and 14.x do not support draft synchronization. Wino keeps drafts in a separate local folder and sends through EAS. Remote drafts are not synchronized.
- Email attachment transport uses complete MIME messages. EAS event attachments, edits or deletions of individual recurring occurrences, online search and directory search are not implemented. Task groups, placements and checklist steps remain local.
- Calendar series and incoming exceptions are projected to Wino's recurrence model. Timezone mapping, recurrence changes, invitation responses and concurrency need server-specific checks before production use.
- Cloud account import/export includes EAS connection metadata without passwords. Compatibility with the separately deployed Wino Account API is unverified.

## Verification

Run the dependency-free regression executable from the repository root:

```powershell
dotnet run --project tests/Wino.Eas.Tests/Wino.Eas.Tests.csproj -p:Platform=x64
```

It exercises the production EAS assembly, real SQLite database initialization, Wino's persistence services and the provider request adapter. HTTP responses are scripted in memory; no real server, credentials or user data are accessed. Each integration fixture uses a separate temporary database and MIME directory, removed after the check. A failed assertion exits with a nonzero status.

The protocol checks cover WBXML, MIME, version negotiation, provisioning, timezone and recurrence mapping. Integration checks cover calendar creation with immediate and delayed server echoes, changed server IDs, restart, concurrent synchronization, existing duplicate reconciliation, calendar isolation, dependent rows, invalid-key recovery and pagination. They also cover mail identity and MIME persistence, flag changes, local draft completion, task/contact deltas and deletions, missing Tasks collections, rejected additions and failed local persistence without advancing the synchronization key. Provider calendar create/update/delete requests verify that client tracking never leaks into wire IDs.

Run the repository's Windows Debug and Release gates and XAML formatter checks. Shared changes also require the Android build and Apple build handoff described in `AGENTS.md`.

Before release, verify on disposable accounts on real EAS servers: first and delta synchronization for each class; restart and invalid-key recovery; remote deletions and cleared properties; read/flag/move/delete; MIME and attachments; sending without replay; local draft retention; contact pictures; task and list mutations; all-day events across DST; recurring exceptions and invitation responses. Test a permissive provisioning policy, a restrictive policy rejection, authentication failures and remote wipe rejection.

Do not replace a signed Store installation to perform these checks. Use the repository's Windows Sandbox flow or a separate development machine.

Protocol references: [Sync and change processing](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ascmd/89449dc4-678c-4deb-9be2-e1dbbc43e2f5), [ghosted properties](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ascmd/a492869c-dad0-4ea7-a2ba-2a252a386ee4), [device provisioning](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-asprov/bdaa71ab-1d98-48b6-b135-e90a4459796c).
