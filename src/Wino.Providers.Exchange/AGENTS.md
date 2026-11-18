# Wino.Providers.Exchange

On-premises Exchange mailboxes over two transports: EWS and MAPI over HTTP. It holds both synchronizers, the MAPI protocol layer, the change processor and error handlers, Exchange autodiscovery, the connection tester, and the Exchange authenticators (NTLM and OAuth). It references `Wino.Core` only. Read [Wino.Core](../Wino.Core/AGENTS.md) first for the request pipeline and the provider registry.

## Hard stops

- **EWS and MAPI are one provider.** `MapiExchangeSynchronizer` derives from `ExchangeSynchronizer`, and `ExchangeProviderModule.CanReuseSynchronizer` decides when a transport change needs a new synchronizer. Keep both transports in this project.
- **Sign-in rules** are under "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md). Do not change how credentials or tokens are stored.
- **No other provider.** This project never references another `Wino.Providers.*` project, and nothing outside the hosts references this one.
- **Do not break data that existing users have already synced.**

## Always

- Register a service only Exchange needs in `AddExchangeProvider` ([Providers/ExchangeProvider.cs](Providers/ExchangeProvider.cs)).
- The MAPI protocol layer is under [MAPI/](MAPI), namespace `Wino.Providers.Exchange.MAPI`. It was its own project and has no implicit usings: each file lists the `System.*` namespaces it needs.
- Sending builds the message with `MailKitSmtpTransport.CreateSmtpMessage` from `Wino.Core`.
- Exchange autodiscovery skips the SCP lookup off Windows. Keep code that needs Active Directory behind that check.
- Every file's namespace is `Wino.Providers.Exchange` plus its folder path.

## Running a change

Run a change against an Exchange account from a script through [Wino.Hosting](../Wino.Hosting/AGENTS.md) when the mail lab offers one; [tools/lab/README.md](../../tools/lab/README.md) lists what the lab has. If no Exchange account is available on the machine, build the affected request from a script, print it before and after, and report "synchronization against an Exchange server pending". State which transport, EWS or MAPI, a result applies to.
