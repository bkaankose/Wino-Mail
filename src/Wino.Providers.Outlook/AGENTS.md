# Wino.Providers.Outlook

Outlook and Microsoft 365 mailboxes over Microsoft Graph: the synchronizer for mail, calendar, contacts, To Do and mail filters, its change processor and error handlers, the Graph HTTP handlers, the push relay registration, and the MSAL authenticator with its token cache. It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline and the provider registry.

## Hard stops

- **Client ids and sign-in.** The verified Entra app, the legacy app that only Windows keeps for accounts with a NULL `OutlookClientId`, and where client ids may live are hard rules under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md). Never migrate an account to another client, and never change how `AccountSetupRunner` records `OutlookClientId`.
- **One way to build a Graph client.** Create every `GraphServiceClient` through `OutlookSynchronizer.CreateGraphClient`, which wraps the adapter in `GraphRequestAdapterWithoutBackingStore`. A client built on a plain adapter turns on change tracking for every client in the process: a property set to null is then sent as an explicit null, and a model serialized twice sends only its changes.
- **The official package only.** Outlook uses `Microsoft.Graph`. Add no generated client and no hand-written copy of a Graph model.
- **No other provider.** This project never references another `Wino.Providers.*` project, and nothing outside the hosts references this one.
- **Request bodies are part of the contract with the server.** Build every request through the official SDK request builders and their Kiota serialization (for example the move body is `{"destinationId": "..."}` from `Move.ToPostRequestInformation`). Compare a request before and after any change to how it is built.

## Always

- Register a service only Outlook needs in `AddOutlookProvider` ([Providers/OutlookProvider.cs](Providers/OutlookProvider.cs)). Describe a difference from other providers as a trait or a module member, never as a type check in shared code.
- Every file's namespace is `Wino.Providers.Outlook` plus its folder path.
- `MsalLogForwarding` stays in `Wino.Authentication`, because the platform sign-in hosts use it. The interactive part of sign-in is each platform's `IOutlookAuthenticationHost`.
- Keep JSON source-generated; the Windows app is Native AOT.

## Running a change

No Outlook test mailbox is set up yet, so a full synchronization cannot run from a script. Until one exists:

- Check how a request is built without a token. The synchronizer's request builders can be called from a script through [Wino.Hosting](../Wino.Hosting/AGENTS.md): build the request before and after your change and compare the URL, headers and body.
- Check response handling by parsing a Graph-shaped JSON document through the same mapping code the synchronizer uses.
- Report "synchronization against a real Outlook mailbox pending".

## Performance

| Limit | Value | Where |
| --- | --- | --- |
| Graph batch | 20 requests | `OutlookSynchronizer.cs` |
| Attachment upload chunk | 320 KB | `OutlookSynchronizer.cs` |

Graph throttles per mailbox. `GraphRateLimitHandler` honors the server's retry delay; do not retry around it.
