# Wino.Platform.Apple

The Foundation-level services that the macOS and iOS apps share: paths, the Keychain store, credential and token stores, the App Store client, notifications, state persistence, the reader runtime and the browser sign-in brokers. It targets `net10.0-macos;net10.0-ios`. A change here ships in two apps.

## Read first

| When you | Read |
| --- | --- |
| Build or check either Apple app | [macOS development](../../docs/harness/development-macos.md) and [iOS development](../../docs/harness/development-ios.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |
| Touch signing, entitlements or provisioning profiles | Hard stops in [Wino.Mail.MacOS](../Wino.Mail.MacOS/AGENTS.md) |

## Hard stops

- Use Foundation-level APIs only. A service that needs AppKit goes to `Wino.Platform.MacOS`; one that needs UIKit goes to `Wino.Platform.iOS`.
- Never reference a presentation project (`Wino.Mail.MacOS`, `Wino.Mail.UIKit*`, `controls/*`).
- Secrets go to the Keychain through `AppleKeychainStore`. There is no plaintext fallback: when the Keychain is unavailable the operation fails and says so.
- The iOS notification extension links `WinoAuthenticationClientIds` from `Wino.Core.Domain`, so that file stays dependency-free.

## Always

- Keep one implementation per contract for both apps. When the two apps need different behavior, split the contract in `Wino.Core.Domain` rather than branching on the platform here.
- Register a new service in both compositions: [Wino.Mail.MacOS/Composition.cs](../Wino.Mail.MacOS/Composition.cs) and the iOS platform registration in `Wino.Platform.iOS`.

## Check your change

- **On a Mac:** the Mac native Debug builds for arm64 and x64, and the iOS Debug Simulator build. Both workloads must be installed. An `osx-*` runtime identifier builds only the macOS target, and an `ios-*` one only the iOS target.
- **On any other machine:** this project does not build there. Report "Mac arm64 and x64 builds and iOS Simulator build pending" with the list of changed files.
