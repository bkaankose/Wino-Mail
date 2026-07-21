# UWP reverse-migration status

This branch contains a buildable production-UI slice of the .NET 10 UWP
reverse migration. It is not a Store cutover candidate yet. Keep
`Wino.Mail.WinUI` in the solution and in release pipelines until every cutover
gate below passes.

## Implemented

- `Wino.Mail.Uwp` uses the modern .NET 10 UWP project shape and WinUI 2.8.7.
- The package preserves the `App` and `CalendarApp` application identities and
  their existing protocol, file, share, tile, badge, and notification ownership.
- `CalendarApp` writes durable activation envelopes and launches the canonical
  `App` entry without creating a second visible window.
- `Wino.Companion` is a windowless, self-contained .NET 10 full-trust process.
  It owns backend DI, SQLite, authentication, synchronization, notifications,
  startup behavior, and the native tray icon.
- The companion and UWP client communicate only through
  `CommunityToolkit.Labs.AppServices`. There is no named-pipe fallback.
- The production Mail, Calendar, Contacts, Settings, onboarding, compose,
  reader, picker, dialog, WebView2, and printing surfaces are ported into the
  single-window UWP shell. Pop-out/window-manager UI is not present.
- Generated RPC proxies and dispatchers cover 23 backend interfaces. Mutating
  requests use durable operation IDs and fail closed if the operation journal
  is ambiguous or corrupt.
- Companion events use bounded request/response polling over the Labs
  AppService connection, with a replay buffer, epoch and sequence-gap
  detection. UI message publication is marshalled through the UWP dispatcher.
- MIME and other large content crosses the process boundary through validated,
  checksummed package-local leases instead of `ValueSet` payloads. SQLite and
  persistent MIME ownership remain in the companion.
- Outlook authentication retains WAM. Interactive operations receive a
  request-scoped UWP CoreWindow HWND that the companion validates against the
  authenticated UWP process before passing it to MSAL. A stale handle fails
  immediately instead of attempting a callback on the occupied AppService
  channel; the next UI request supplies and revalidates a fresh handle.
- Mica is the default through WinUI 2 `BackdropMaterial`; UWP Acrylic, solid,
  and image backgrounds remain available. Unsupported WinUI 3-only backdrop
  variants are normalized to their supported equivalents.
- Mail/Calendar cold and warm entry activation has been exercised against the
  installed loose package. The Calendar bootstrap process exits and the
  canonical UWP process switches to the production Calendar shell.
- The Labs host owns full-trust process startup, avoiding duplicate-launch
  connection churn. Killing the companion while the UWP UI is open causes the
  next activation/RPC to launch, handshake, and resynchronize with a new
  companion process.
- Window close and tray Exit now ask the active production Compose page to
  persist editor/MIME changes before the bounded backend flush. Tray Exit then
  detaches and closes the UWP client instead of leaving a UI that immediately
  restarts the companion.
- Debug builds compile for x86, x64, and ARM64. The AppService contract suite
  passes 13/13 tests; the core suite passes 234 tests with 10 opt-in live
  provider tests skipped; the Mail ViewModel suite passes 45/45 tests.
- First-connection reliability: the companion registers its complete RPC table,
  opens the AppService transport before backend initialization and gates every
  backend-dependent RPC (generated dispatchers and notification handlers) on a
  backend-readiness task with a bounded 30 second wait. `OpenAsync` retries with
  backoff to absorb the UWP activation race. The UWP client waits up to
  30 seconds for the very first companion connection (5 seconds for reconnects
  to an already-running companion) and clears a failed launch task so later
  activations retry. Shell menu construction and the initial folder navigation
  no longer await companion RPCs; synchronization progress is applied in the
  background when the companion answers.
- Startup latency: translation and the read-only database initialize
  concurrently while XAML resources load and the companion launches. A
  persisted wallpaper theme applied before activation is no longer re-decoded
  after the first frame, which removes the backdrop flash on wallpaper themes.
- Mode-driven taskbar identity: switching the shell between Mail and Calendar
  asks the companion (RPC `window.set-taskbar-identity.v1`) to re-stamp the
  visible window's AUMID via `SHGetPropertyStoreForWindow`, targeting the
  hosting `ApplicationFrameWindow` (with a CoreWindow fallback lookup by
  process id). Best effort: a failed switch leaves the previous taskbar icon.
- Release MSIX packages can be produced for x86, x64, and ARM64 with
  `scripts/package-uwp.ps1`. Release packages for all three architectures have
  been built successfully; the packaged manifest contains both application
  entries and the payload contains the headless companion executable.

## Not implemented yet

- Installed-package protocol, file, share-target, toast-action, badge,
  JumpList, and startup-task routes still require a full system matrix. `.ics`
  and `webcal` currently route to Calendar without implementing a new import or
  subscription workflow; this matches the previous UI's effective behavior.
- Tray menu and close-preference combinations need system tests with real
  accounts and an active synchronization. Outlook WAM with the validated UWP
  HWND still needs provider-backed end-to-end testing.
- Partner Center preflight, update-in-place data preservation, and the cold
  start/working-set performance gate have not been run.
- Cross-process AUMID re-stamping of the hosted UWP window has not been
  validated on an installed package across Windows builds. If the shell
  overrides the property on re-activation, the fallback is accepting the
  stale icon or converting `CalendarApp` into a full-trust launcher stub (see
  the taskbar identity notes above; that changes the entry's activation
  pipeline and needs Store update validation).
- The one-second bootstrap window/splash when launching the Calendar entry
  while Mail runs is inherent to activating a second UWP application identity;
  it exits after handing off. Eliminating it entirely requires the launcher
  stub approach.

## Cutover gates

Do not remove `Wino.Mail.WinUI` or publish the UWP package until all of these are
complete:

1. Exercise every activation route with both cold and warm launches and prove
   that only one visible window exists.
2. Validate durable mutation recovery, event replay,
   large payload leases, and companion close behavior on an installed package.
3. Validate interactive and silent Outlook WAM flows, including stale/forged
   HWND rejection and UI closure during authentication.
4. Pass x86, x64, and ARM64 package tests, WACK, and Partner Center preflight
   without changing either application entry.
5. Pass update-in-place tests from the current Store build with databases, MIME,
   settings, tokens, tiles, and protocols intact.
6. Demonstrate at least 20% lower median cold-start-to-interactive time and 20%
   lower combined steady private working set than the WinUI 3 build on the same
   machine and dataset.

## Verification commands

```powershell
dotnet test Wino.AppServices.Tests/Wino.AppServices.Tests.csproj -c Debug /p:Platform=x64
dotnet test Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64
.\scripts\package-uwp.ps1 -Platform x64 -Configuration Release -OutputDirectory artifacts\uwp
.\scripts\package-uwp.ps1 -Platform x86 -Configuration Release -OutputDirectory artifacts\uwp
.\scripts\package-uwp.ps1 -Platform arm64 -Configuration Release -OutputDirectory artifacts\uwp
```
