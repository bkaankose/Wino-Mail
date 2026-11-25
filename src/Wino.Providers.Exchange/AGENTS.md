# Wino.Providers.Exchange

On-premises Exchange mailboxes over three transports: EWS, MAPI over HTTP and Exchange ActiveSync (EAS). It holds the three synchronizers, the MAPI protocol layer, the ActiveSync client and engine, the change processor and error handlers, Exchange autodiscovery, the connection tester, and the Exchange authenticators (NTLM and OAuth). It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline and the provider registry.

## Hard stops

- **One provider, three transports.** `MapiExchangeSynchronizer` derives from `ExchangeSynchronizer` (EWS); `ActiveSyncExchangeSynchronizer` is separate. `ExchangeProviderModule` picks the synchronizer from the account's transport, and `CanReuseSynchronizer` decides when a transport change needs a new one. Keep all three in this project.
- **ActiveSync is opt-in.** Automatic transport detection never selects it; the user chooses it. It needs a password (no OAuth) and targets Exchange 2016, 2019 and SE on premises; Microsoft 365 uses the Graph provider.
- **Sign-in rules** are under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md). Do not change how credentials or tokens are stored.
- **No other provider.** This project never references another `Wino.Providers.*` project, and nothing outside the hosts references this one.
- **Do not break data that existing users have already synced.**

## Always

- Register a service only Exchange needs in `AddExchangeProvider` ([Providers/ExchangeProvider.cs](Providers/ExchangeProvider.cs)).
- The MAPI protocol layer is under [MAPI/](MAPI), namespace `Wino.Providers.Exchange.MAPI`. It was its own project and has no implicit usings: each file lists the `System.*` namespaces it needs.
- The ActiveSync transport is under [Synchronizers/Exchange/ActiveSync/](Synchronizers/Exchange/ActiveSync): `EasClient` (HTTP, WBXML, provisioning, policy), `EasSynchronizationEngine` (FolderSync, Sync, recovery, mutations, drafts, calendar) and the mappers. Its per-account state is in its own tables (`EasAccountState`, `EasCollectionState`, `EasItemShadow`, `EasPendingSync`, `EasClientAnswer`, `EasDraftState`), deleted with the account and reset on a transport change.
- Sending builds the message with `MailKitSmtpTransport.CreateSmtpMessage` from `Wino.Core`, on every transport.
- Exchange autodiscovery skips the SCP lookup off Windows. Keep code that needs Active Directory behind that check.
- Every file's namespace is `Wino.Providers.Exchange` plus its folder path.

## Changing transports

A transport change is one shared operation (`ExchangeTransportChange` in the ViewModels, `ResetExchangeTransportStateAsync` in the account service): sync state and the mail cache are reset, and `MailItemFolder` rows stay so the next folder pass re-keys them in place. Folder ids and their settings (pinned, hidden, synchronization, order, a special type the user assigned) must survive every direction.

- EWS and MAPI re-key by the folder's entry id: MAPI records it (`MapiEntryId`, with `MapiFolderId`), EWS gets it from ConvertId (HexEntryId).
- Matching by server role and path (`ExchangeTransportRekey.MatchFolders`) is only for rows going to or from ActiveSync, whose server ids are opaque.
- Each transport's folder pass re-keys before it deletes missing folders, sends `AccountFolderConfigurationUpdated` when its hierarchy changes, and does nothing extra in a steady-state pass.

## ActiveSync conformance

Every ActiveSync behaviour follows Microsoft's open specifications (MS-ASCMD, MS-ASPROV, MS-ASHTTP, MS-ASWBXML, MS-ASEMAIL, MS-ASCAL, MS-ASAIRS, MS-ASDTYPE, and MS-OXWSCVTID or MS-OXCDATA where ids cross transports).

- Read the section on learn.microsoft.com before you implement or change a behaviour, and cite it in the commit body.
- Where the protocol defines a mechanism, use it: no heuristics, no name matching where an id or role exists, no swallowed statuses, no guessed fallbacks, no retrying until something works.
- Honour every status the protocol gives, with the resolution the specification prescribes (for example status 3: delete the client's copy below 16.1 and synchronize from key 0; status 8: FolderSync, Sync, then send again; status 12: FolderSync and send again).
- A Sync with client changes is stored before it is sent and sent again unchanged, with the same key, when its response is lost.
- ClientIds are stable for the same draft version or event, so a request sent again is recognized as a duplicate.
- If the lab server contradicts the specification, capture the raw request and response and report it instead of coding around it. Code comments explain only risks and uncommon decisions; the citation belongs in the commit.

## Running a change

Run a change against an Exchange account from a script through [Wino.Hosting](../Wino.Hosting/AGENTS.md) when the mail lab offers one; [tools/lab/README.md](../../tools/lab/README.md) lists what the lab has. If no Exchange account is available on the machine, build the affected request from a script, print it before and after, and report "synchronization against an Exchange server pending". State which transport, EWS, MAPI or ActiveSync, a result applies to, and for ActiveSync the negotiated protocol version.
