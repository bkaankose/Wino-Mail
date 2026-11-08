# Wino Exchange lab (Hyper-V)

One Hyper-V VM running Exchange Server SE on its own Active Directory forest, for testing Wino's Exchange support (Autodiscover v2, EWS, MAPI over HTTP, NTLM) against a real on-premises server.
It is separate from the [Docker IMAP lab](../local-lab/README.md) and talks to nothing outside the host except Windows activation and the prerequisite downloads during `create`.

Every password is `1`, as in the Docker lab, except the AD restore-mode password (`Wino-Lab-Dsrm-2026!`), which Windows forces to be complex.

## What you get

| Item | Value |
|---|---|
| VM | `wino-exchange`, Generation 2, 4 vCPU, 10 GB fixed RAM, 150 GB dynamic disk in `D:\Hyper-V\wino-exchange` |
| Network | Internal switch `WinoExchangeLab` with NAT `WinoExchangeLabNat` on `192.168.250.0/24`; host `192.168.250.1`, VM **`192.168.250.10`** (fixed) |
| OS | Windows Server 2025 Datacenter Evaluation (Desktop Experience), 180 days |
| Directory | AD forest `exchange.test`, NetBIOS `EXCHANGE`, server `EX1.exchange.test` (domain controller and DNS) |
| Exchange | Exchange Server SE RTM (15.2.2562.17), Mailbox role, trial mode (180 days, no product key), organization `Wino Lab` |
| Mail domain | **`exchange.internal`** (authoritative, default). Not `exchange.test`: Exchange transport rejects recipients in RFC 2606 TLDs with `501 5.1.4 Recipient address reserved by RFC 2606` |
| Mailboxes | `alice@exchange.internal`, `bob@exchange.internal`, `carol@exchange.internal`; the UPN equals the address, and `EXCHANGE\alice` also signs in |
| Admin | `EXCHANGE\Administrator` / `1`; EAC at `https://mail.exchange.internal/ecp` |
| Certificate | Self-signed `CN=mail.exchange.internal` with SANs `ex1.exchange.test`, `mail.exchange.internal`, `autodiscover.exchange.internal`, `exchange.internal`, IP `192.168.250.10`; exported to `.state/wino-exchange.cer` |
| Checkpoint | `exchange-installed`: the verified clean state that `restore` returns to |

| Endpoint | URL |
|---|---|
| Autodiscover v2 (what Wino calls) | `https://autodiscover.exchange.internal/autodiscover/autodiscover.json?Email=alice@exchange.internal&Protocol=EWS` |
| Autodiscover v1 (POX) | `https://autodiscover.exchange.internal/Autodiscover/Autodiscover.xml` |
| EWS | `https://mail.exchange.internal/EWS/Exchange.asmx` (NTLM, Negotiate, Basic) |
| MAPI over HTTP | `https://mail.exchange.internal/mapi` (NTLM, Negotiate, Basic) |
| OWA | `https://mail.exchange.internal/owa` |
| SMTP submission | `mail.exchange.internal:587` (STARTTLS, authenticated, no rate limit) |

Every URL also works with `192.168.250.10` in place of the host name, because the certificate carries the IP.

The AD DNS zone `exchange.internal` has `mail`, `autodiscover`, the apex A record, an MX record and `_autodiscover._tcp` SRV.
The host does not use that DNS server, and Wino resolves SRV records through public DNS over HTTPS, so on the host only the hosts-file names work; the SRV path cannot be tested from the host.

## Requirements

- Hyper-V enabled and an administrator account; each command elevates itself (UAC prompt where UAC is on).
- About 12 GB of free RAM and 60 GB of disk on `D:`.
- `D:\Hyper-V\iso\ExchangeServerSE-x64.iso` ([Download Center](https://www.microsoft.com/download/details.aspx?id=108244)) and `D:\Hyper-V\iso\WindowsServer2025-Eval.iso` ([Evaluation Center](https://www.microsoft.com/evalcenter/download-windows-server-2025)). Pass `-IsoRoot` for another folder.
- No other `NetNat` network overlapping `192.168.250.0/24`.

## Commands

```powershell
.\scripts\lab\windows\exchange-lab.ps1 create    # build everything (about 1.5 hours), then checkpoint 'exchange-installed'
.\scripts\lab\windows\exchange-lab.ps1 trust     # trust the server certificate for your user and map the names in the hosts file
.\scripts\lab\windows\exchange-lab.ps1 verify    # delivery probe between all mailboxes, then Autodiscover and EWS from the host
.\scripts\lab\windows\exchange-lab.ps1 status
.\scripts\lab\windows\exchange-lab.ps1 stop
.\scripts\lab\windows\exchange-lab.ps1 start
.\scripts\lab\windows\exchange-lab.ps1 checkpoint   # extra 'manual-<timestamp>' checkpoint
.\scripts\lab\windows\exchange-lab.ps1 restore      # back to the clean 'exchange-installed' state
.\scripts\lab\windows\exchange-lab.ps1 untrust
.\scripts\lab\windows\exchange-lab.ps1 remove -Force   # delete VM, disk, switch and NAT; ISOs stay
```

`create -StartAt <phase>` resumes a failed build at `guest`, `forest`, `domain`, `exchange`, `configure` or `mailflow` instead of starting over.
Elevated runs write their log to `tools/exchange-lab/.state/<command>.log`; Exchange setup's own log is `C:\ExchangeSetupLogs\ExchangeSetup.log` in the VM.
Exchange services take 5-10 minutes to settle after a start or restore; `status` lists the automatic services that are not running yet.

## Add the lab account in Wino

1. Run `trust` once.
2. In Wino, add `alice@exchange.internal` with password `1` and choose Exchange. Autodiscover v2 resolves EWS at `https://mail.exchange.internal/EWS/Exchange.asmx`.
3. Use `bob` or `carol` (OWA, or a second Wino account) to send mail to Alice; delivery is internal to the VM.

## Lab-only choices

- Exchange runs on the domain controller. Microsoft does not support that for production; it keeps the lab to one VM.
- Transport resource monitoring (back pressure) is off in `EdgeTransport.exe.config`. With 10 GB of RAM the memory threshold would otherwise stop mail submission.
- The client receive connectors have no message rate limit (the default is 5 messages a minute per user).
- The AD password policy has no complexity, length, expiry or lockout, so `1` works everywhere.
- EWS and MAPI allow Basic authentication in addition to NTLM and Negotiate.
- The build reboots the VM from the host (`Stop-VM`/`Start-VM`); a `Restart-Computer` sent over PowerShell Direct after domain promotion was lost and left NTDS unstarted.
