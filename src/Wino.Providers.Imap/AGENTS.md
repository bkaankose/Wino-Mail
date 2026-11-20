# Wino.Providers.Imap

IMAP and POP3 mailboxes over MailKit: both synchronizers, the IMAP connection pool, server quirks, the POP3 adapter, the setup probes and resolver, the server connection test, the IMAP part of the change processor and the error handlers. It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline and the provider registry.

## Hard stops

- **Follow the RFC and stay generic.** A fix applies to every conforming server. Add no new per-server exception; for a server that misbehaves, write a note for the owner with the server, the command and the reply.
- **Do not break data that existing users have already synced**, and make no schema change for a synchronizer fix.
- **OAuth for IMAP providers** is registered in `src/Wino.Services/Configuration/known-imap-providers.json`. The rules for client ids are under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md).
- **No other provider.** This project never references another `Wino.Providers.*` project. An IMAP account with a calendar or contacts reaches CalDAV and CardDAV through the engine contracts in `Wino.Core.Domain`, never through `Wino.Providers.Dav`.

## Always

- Register a service only IMAP or POP3 needs in `AddImapProvider` ([Providers/ImapProvider.cs](Providers/ImapProvider.cs)). It registers two modules, IMAP4 and POP3.
- `ImapChangeProcessor` derives from `CalDavChangeProcessor` in `Wino.Core` and adds only what is IMAP-specific. Calendar storage belongs to the base class.
- SMTP sending (`MailKitSmtpTransport`, its connection policy, `MailKitOAuthAuthenticator`) and `WinoProtocolLogger` stay in `Wino.Core`, because Exchange and a ViewModel use them.
- Every IMAP command goes through a client from `ImapClientPool`. Return the client to the pool on every path, including failure.
- Every file's namespace is `Wino.Providers.Imap` plus its folder path.

## Running a change

Run every change against the mail lab from a script: [Wino.Hosting](../Wino.Hosting/AGENTS.md) adds a leased lab account through the app's setup path and synchronizes it. The lab has several IMAP servers with different behavior and a POP3 server; [tools/lab/README.md](../../tools/lab/README.md) says which server covers what. Read the result back from the database and, for a change that writes to the server, from the server through a second connection.

## Performance

| Limit | Value | Where |
| --- | --- | --- |
| Connection pool | 5 by default, 1 to 10 | `Integration/ImapClientPool.cs` |
| New-message fetch | 200 per batch | `Synchronizers/ImapSync/UnifiedImapSynchronizer.cs` |
| Parallel folder sync | 3 clients | `Synchronizers/ImapSynchronizer.cs` |
| Full UID reconcile | every 12 hours | `Synchronizers/ImapSync/UnifiedImapSynchronizer.cs` |

Many servers limit connections per account. Raising a limit here can lock a user out of their mailbox.
