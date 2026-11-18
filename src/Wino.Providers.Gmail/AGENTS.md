# Wino.Providers.Gmail

Gmail mailboxes over Google's REST APIs: the synchronizer for mail, calendar, contacts and tasks, its change processor and error handlers, the REST clients for Gmail, Calendar, People, Tasks and Drive, the push relay registration, and the Google mailbox profile service. It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline and the provider registry.

## Hard stops

- **Never touch the Google UWP OAuth client.** That rule, the per-platform Google clients, and the rule that each stored token refreshes with the client that issued it are under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md).
- **No Google NuGet package.** The REST clients under [Google/](Google) are hand-written, mirror the official `Google.Apis.*` namespaces under `Wino.Providers.Gmail.Google` (`Google/Apis/Gmail/v1/...`) with the official type names, and use source-generated JSON. Google's own library reads its types by reflection and fails under trimming and Native AOT: in a trimmed build five of six operations threw and one sent an empty request body without an error. Keep the official names and the mirrored folders, so the swap is small when Google ships a trimmable library.
- **Quota is shared by every Wino user.** Google Tasks and Google contacts use a six-hour window for automatic syncs only. A manual sync always runs, and an edit goes to Google at once. `SyncCoordinator` owns that window. Never show a quota or a rate limit to the user.
- **No other provider.** This project never references another `Wino.Providers.*` project, and nothing outside the hosts references this one.

## Always

- Register a service only Gmail needs in `AddGmailProvider` ([Providers/GmailProvider.cs](Providers/GmailProvider.cs)). `AddLoopbackGoogleAuthorizationCodeReceiver` lives here too; the Windows head calls it.
- `GmailAuthenticator` stays in `Wino.Authentication`, because the Android platform project uses the concrete class. The interactive part of sign-in is each platform's `IGoogleAuthorizationCodeReceiver`.
- When you add a field to a REST model, add it to the JSON source-generation context in `Google/GoogleApiClient.cs`.
- The `Microsoft.Graph.Core` reference exists only because `Base64UrlEncoder` comes with it. Do not use anything else from it.
- Every file's namespace is `Wino.Providers.Gmail` plus its folder path.

## Running a change

No Gmail test mailbox is set up yet, so a full synchronization cannot run from a script. Until one exists, build the request your change affects from a script through [Wino.Hosting](../Wino.Hosting/AGENTS.md), print its URL and body before and after, and parse a Google-shaped JSON reply through the same model. Report "synchronization against a real Gmail mailbox pending".

## Performance

| Limit | Value | Where |
| --- | --- | --- |
| Gmail batch | 10 requests | `GmailSynchronizer.cs` |
| Message id listing | 1,000 per page | `GmailSynchronizer.cs` |

A Gmail message can sit in several folders at once (`StoresMessageInSeveralFolders`). Storage code handles that; do not download the same message once per label.
