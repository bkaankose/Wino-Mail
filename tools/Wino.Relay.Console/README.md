# Wino Relay socket console

A terminal client for the desktop relay socket (`src/Wino.PushRelay.Socket`). It signs in as the Wino Account the app
on this machine is signed in to, registers **its own** relay device (named "Wino Relay Console (<machine>)", with its own
P-256 socket key), opens the SignalR socket to WinoRelay and prints every event it receives, decrypted, in green.

```bash
# macOS (needs ~/.dotnet with the macos workload; the first run asks for Keychain access to the app's tokens)
~/.dotnet/dotnet run --project tools/Wino.Relay.Console -- --api prod
```

```powershell
# Windows
dotnet run --project tools/Wino.Relay.Console -p:Platform=x64 -- --api prod
```

## What it reads and writes

- **Reads** the app's `Wino210.db` read-only (`~/Library/Application Support/com.winomail.macos` or the sandbox
  container on the Mac, `%LOCALAPPDATA%\Packages\<family>\LocalState` on Windows) for the signed-in `WinoAccount`. On
  the Mac the tokens are Keychain items (`com.winomail.macos.v1.wino-tokens`), restored through the Apple platform layer.
- **Never refreshes** the Wino tokens. The API rotates refresh tokens, so a refresh from here would sign the app out.
  When the access token has expired, open Wino Mail (it refreshes) and press `r`.
- **Writes** only its own folder (`%LOCALAPPDATA%\WinoRelayConsole` or `~/.local/share/WinoRelayConsole`):
  `device-identity.json` and `socket-key.p8` (plain PKCS#8, fine for a developer tool). The app's relay device and key
  stay untouched, so the app and the console can both be registered at the same time.

## macOS notes

- The bundle is signed with the "Apple Development: Created via API" identity (`CodesignKey` in the csproj) so that
  Keychain's "Always Allow" for the app's token items survives rebuilds; build with `-p:EnableCodeSigning=false` on a
  Mac without that certificate (every build then prompts again).
- The macOS app host installs an AppKit synchronization context on the main thread, and a console never pumps its run
  loop, so `Main` runs everything on a thread-pool thread. Apple's `NSUrlSession` handler needs that run loop too, so
  the project sets `UseNativeHttpHandler=false`.
- The app refreshes its access token only when it calls the API (for example at launch). When the console reports an
  expired token, quit and relaunch Wino Mail, then press Enter.

## Commands

| Key | Action |
| --- | --- |
| `t` | `POST /v1/devices/me/push-test`; the `test` event arrives over the socket |
| `a [eid…]` | acknowledge events (automatic unless `--no-ack`) |
| `s` | socket state, last hello, pending acks, account token validity |
| `d` | `GET /v1/devices/me` as JSON |
| `c` / `x` | connect / stop the socket |
| `r` | re-read the app's tokens after the app refreshed them |
| `forget` | delete the console device on the relay and locally, then register a new one |
| `q` | stop and quit |

When `hello` reports unacknowledged events, the client fetches them over HTTP (`GET /v1/devices/me/events?unacked=true`)
and prints them like live events, marked `(replayed)`; the console acknowledges them the same way.

Encrypted events need the relay's `eid` in the envelope (the AES-GCM associated data); events that cannot be decrypted
are printed in red and left unacknowledged.
