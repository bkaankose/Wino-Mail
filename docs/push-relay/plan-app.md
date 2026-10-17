# Push relay: application plan (all platforms)

Companion to [push-relay-design.md](../push-relay-design.md). Backend plans: [API](plan-api.md), [relay](plan-relay.md).
This plan covers the client repository only. Every file reference was verified on 2026-10-17 against
`feature/Android`.

## What exists today (verified)

- `PushRelayRegistrationService` (`src/Wino.Services/PushRelay/PushRelayRegistrationService.cs`) runs
  coalesced passes, registers the device, syncs mailboxes and handles `device_revoked`. Its gate treats
  "no Wino Account session" as `SignInRequired` and deletes the device.
- The only mailbox builder is `ImapPushRelayMailboxRegistrationBuilder`. The create request record is
  IMAP-only (`PushRelayContracts.cs:88`). `PushRelayNotificationHandler` logs `mailbox.renew` as
  unsupported.
- `PushRelayTokenProvider` gets every relay token from `POST api/v1/relay/token` with the Wino Account
  bearer. There is no device credential.
- Desktop socket client exists and is registered on Windows and macOS; nothing starts it.
- Windows registers as platform `windows` with `NullPushTokenProvider` and `PushRelayOptIn = true`.
  macOS has `RemoteNotifications = true` and `PushRelayOptIn = true`. iOS and Android have
  `RemoteNotifications = true`, `PushRelayOptIn = false`.
- Sync timers: `EmailSyncIntervalMinutes` (default 3) and `CalendarSyncIntervalMinutes` (default 5) drive
  `ApplicationRuntime` loops. Contacts and To Do run on a fixed 6 hour rare-mode loop.
- IMAP IDLE is switched off for every account by
  `SynchronizerFactory.IsImapIdleTemporarilyDisabled = true` (temporary flag from 14 Oct 2026).
- Gmail goes through the hand-written `GmailRestClient` (no `users.watch`). Graph uses a trimmed Kiota
  client with `/me` routes only (no `/subscriptions`).
- What's New opens only from the shell button; nothing opens a window on launch. There is no stored
  "last launched version". `MailAccount.CreatedAt` is nullable and NULL for old accounts.
- `IntelligenceConsentPolicyDialog` plus `IWinoDialogService.ShowIntelligenceConsentPolicyDialogAsync`
  is the consent pattern to copy (WebView2 policy, acknowledgement checkbox, deferral until the accept
  callback succeeds).
- No iOS Notification Service Extension exists. `ShellTip` TeachingTip exists in `WinoAppShell.xaml`
  but is never opened.

## Shared layer

### S1. Sync method setting

- `enum SyncMethod { Timer, Relay }` in `Wino.Core.Domain`.
- `IPreferencesService.SyncMethod`, stored as a preference (exported with settings). Add
  `PlatformSetting.SyncMethod` with availability `ConfigurableSyncIntervals` (desktop only). On
  platforms without it, the shared code reads `Relay`.
- Default when unset: `Relay` on fresh installs. The existing-user migration (W1) writes `Timer` before
  the first relay pass can run.
- `ApplicationRuntime`:
  - `Timer`: today's behavior.
  - `Relay`: mail and calendar loops use a fixed 5 minute fallback interval and run only while the relay
    socket is not connected (desktop) or always on mobile when the app is foreground. Contacts and To Do
    loops are untouched.
  - Expose `IRelayHealth` (Connected, FallbackActive, LastEventUtc) for the shell indicator.

### S2. Device identity and attestation

- `IDeviceAttestationProvider` in `Wino.Core.Domain`: `Kind` (`AppAttest`, `PlayIntegrity`, `None`),
  `IsSupported`, `PrepareAsync(nonce)` returning the attestation blob. Implementations:
  `Wino.Platform.iOS` (DCAppAttestService; `isSupported` false in Simulator and on macOS),
  `Wino.Platform.Android` (Play Integrity standard request, token provider prepared at startup),
  Windows and macOS return `None`.
- `IPushRelayDeviceCredentialStore`: stores the API-issued device credential (opaque secret) in the
  platform secure store (Keychain, DPAPI file, Android Keystore-backed prefs). Separate from the relay
  device identity store, which keeps the relay device id.
- `PushRelayTokenProvider` gains two paths:
  1. Device path: `POST api/v1/relay/devices/challenge` → attest → `POST api/v1/relay/devices`
     (first time) → `POST api/v1/relay/token` with the device credential. Cache until 2 minutes before
     expiry as today.
  2. User path (existing): Wino Account bearer. Used when the device is linked, and as the fallback when
     attestation is `None` on mobile (Simulator, rooted device, sideloaded build) so the user is asked to
     sign in before IMAP or DAV registration.
- `PushRelayRegistrationService` gate: `SignInRequired` only when the device path is unavailable
  (attestation unsupported on a platform that must attest) and no Wino session exists. Otherwise a
  missing Wino session is `Enabled`.
- Relay device registration sends the API device id so the relay row uses the same id (see relay plan
  R2). `PushRelayRegisterDeviceRequest` gets `DeviceId`.
- Sign-in: after `WinoAccountSignedIn`, call `POST api/v1/relay/devices/{id}/link`, then switch the
  token provider to the user path once `GET /v1/devices/me` reports the owner. Sign-out: call
  `unlink`, switch back to the device path, keep registrations. Remove today's "delete device on
  sign-out" behavior (`SignOutAsync` becomes unlink).

### S3. Gmail and Graph builders

- `PushRelayCreateMailboxRequest` becomes a union: `Imap`, `Gmail { AccessToken, WatchExpiresAt }`,
  `Graph { SubscriptionId, ClientState, ExpiresAt, EmailAddress, Folder, Resource }`.
- `GmailPushRelayMailboxRegistrationBuilder` (`Wino.Core`, because it needs `GmailRestClient` and
  `IGmailAuthenticator`): add `users.watch` to `GmailRestClient` with `topicName` from
  `PushRelayWellKnown.GmailTopic` and `labelIds: ["INBOX"]`; store `historyId` and `expiration`; build
  the registration with the access token from `IAuthenticator.GetTokenInformationAsync`. Fingerprint:
  SHA-256 of `email\0historyId-bucket` so a renewed watch re-registers.
- `GraphPushRelayMailboxRegistrationBuilder` (`Wino.Core`): add a minimal `/subscriptions` POST,
  PATCH and DELETE to the Graph layer (hand-written request builder next to the Kiota client, or extend
  `kiota-lock.json` to include `/subscriptions`). One subscription per account for
  `/me/mailFolders/inbox/messages`, `changeType: created,updated,deleted`, `notificationUrl` and
  `lifecycleNotificationUrl` from the well-known document, `clientState` random 128 chars stored per
  account in the secure store, `expirationDateTime` = now + 4230 minutes. Calendar and contacts
  subscriptions are a later phase.
- Renewal: handle `mailbox.renew` in `PushRelayNotificationHandler` by requesting a
  `CredentialsChanged` pass for that account; the hourly check-in also renews anything within 12 hours
  of `RegistrationExpiresAt`. Expired-by-a-week cases simply re-create.
- Desktop IMAP is not sent to the relay (the relay refuses it). `ImapPushRelayMailboxRegistrationBuilder`
  returns not-eligible when `IPlatformCapabilities.RemoteNotifications == false` or the platform is
  desktop; the Mac head is desktop too.

### S4. Privacy acceptance version

- `IPushRelayPolicyConsent`: `AcceptedVersion` (config store, device local) and the current policy
  version constant in `Wino.Core.Domain` (`PushRelayDefaults.PolicyVersion`).
- Fresh installs: the first launch writes the current version; no dialog is ever shown. The store listing
  and website carry the updated policy.
- Existing Windows users: the version is unset at upgrade, which is what triggers W1.
- A later policy bump re-prompts only on platforms with a sync method picker (desktop); mobile relies on
  the store policy.

### S5. Settings surface

- Notifications page: the sync method picker (desktop), status line, mailbox count, test button, link to
  the web portal. Remove the per-platform relay toggle (`IsPushRelayToggleVisible`) in favor of the
  picker; `PushRelayOptIn` becomes unused and is removed from `IPlatformCapabilities`.
- Account pages: status-only row ("Instant updates: on / needs password / not available").
- Wino Account page: active mailboxes and linked devices from the relay portal endpoints
  (`GET /v1/me/devices`, `GET /v1/me/mailboxes`), visible only when signed in.

## Windows

### W1. Existing-user introduction

**Detection.** Two facts are enough and both exist at startup:

1. `IPreferencesService.SyncMethod` has no stored value (new key in this version).
2. `DatabaseService.ReadLifecycleAsync().AccountCount > 0`.

Both true means the install had accounts before this version. Fresh installs reach the first account
with `SyncMethod` already written (default `Relay`), so the window never shows for them. The check
runs once in `App.xaml.cs` after the migration prompt and before the shell loads, writes
`SyncMethod = Timer` immediately, and sets a config flag `Relay.IntroductionPending = true`. No
version comparison is needed, which avoids the missing "last launched version" problem.

**Window.** `RelayIntroductionWindow` modeled on `WhatsNewWindow` (WinUIEx `WindowEx`, 880×640,
`RootFrame` hosting `RelayIntroductionPage`, same title bar drag region). Shown through
`WindowManager.ShowThemedWindowAsync(WinoWindowKind.RelayIntroduction, ...)` when
`Relay.IntroductionPending` is true and the shell has finished loading. Shown once: the flag clears on
any outcome. The `ShellTip` TeachingTip stays unused; a window carries the two cards better.

**Page content.** Two large selectable cards, no provider or credential wording:

- Timer based: "Synchronizations are performed every N minutes on your computer. Notifications are not
  instant. Rate limits apply." N is the user's current `EmailSyncIntervalMinutes`.
- Relay based: "Wino listens to your mailboxes and lets you know. Instant notifications."

A single Continue button. Cards and copy come from `Translator` strings added to
`src/Wino.Core.Domain/Translations/en_US/resources.json`.

**Flow.**

1. Timer selected → write `SyncMethod = Timer`, clear the pending flag, close.
2. Relay selected → `IWinoDialogService.ShowRelayPolicyDialogAsync(policyUri, acceptAsync)`, a new
   `RelayPolicyDialog` in `src/Wino.Mail.WinUI/Dialogs` cloned from `IntelligenceConsentPolicyDialog`.
   `acceptAsync` writes `AcceptedVersion`, writes `SyncMethod = Relay`, and returns true.
   - Accepted → clear the pending flag, close the window, request `PushRelaySyncReason.OptInChanged`.
     The pass registers the device (device path, no attestation on Windows), creates the Gmail watches
     and Graph subscriptions for every eligible account, and starts the socket. IMAP accounts stay on
     the local IDLE client; nothing is uploaded from Windows.
   - Declined → the dialog closes and the window returns to the two cards with nothing selected.
3. Window closed without choosing → `SyncMethod` stays `Timer`, the pending flag clears. The picker in
   Settings > Notifications remains the way in.

**Why not detect by version.** `WhatsNew_LastOpenedVersion` is written only when the user opens
What's New. `PushRelay.State.LastAppVersion` is written only after a relay registration.
`MailAccount.CreatedAt` is NULL for accounts older than the column. None of them is reliable; the
unset preference plus account count is.

### W2. Relay mode on Windows

- Start `IWinoRelaySocketClient` from `ApplicationRuntimeInitialization` when `SyncMethod == Relay`
  and the relay device is registered; stop it when the method changes to `Timer`. Events map to
  `IPushRelayNotificationHandler.HandleAsync` (same as a push). Ack after handling.
- Fallback: `IRelayHealth.FallbackActive` is true while the socket is `Reconnecting` or `Stopped` with
  `SyncMethod == Relay`; the 5 minute mail and calendar loops run only then.
- Shell indicator: a small red dot on the sync button when `FallbackActive`; its flyout says the relay is
  down and notifications may be degraded. Hidden otherwise. Flyout text from `Translator`.
- Re-enable IMAP IDLE: remove `IsImapIdleTemporarilyDisabled`. See open decision D2 for whether IDLE
  runs in both modes.
- Settings > Notifications: the picker switches modes live; switching to `Relay` on a fresh install does
  not show the policy dialog (S4); switching on an install whose `AcceptedVersion` is unset or older
  shows it.
- Verify the relay accepts `platform: windows` with no push token and a socket key (the registration
  service still carries a "Windows today" rejection path). Remove that path once confirmed.

## macOS

- Same desktop behavior as Windows (W2) with the Keychain socket key store. App Attest is not supported
  on macOS, so the device path registers with attestation `None`; macOS sends only Gmail and Graph
  registrations, so no attestation is needed.
- `RemoteNotifications` stays true so APNs continues to work as the secondary channel; the socket is
  primary while the app runs.
- No introduction window: the Mac app is unreleased. `SyncMethod` defaults to `Relay`.

## iOS and iPadOS

- No picker, no dialog. `SyncMethod` is read as `Relay`.
- `IDeviceAttestationProvider` with App Attest: generate a key once, attest with the API nonce, store
  the key id in the Keychain, assert on the device-token call. When `isSupported` is false (Simulator),
  fall back to requiring a Wino Account sign-in for IMAP and DAV registration and show
  `NotificationSettings_PushRelay_SignInRequired`.
- Entitlement `com.apple.developer.devicecheck.appattest-environment`: `development` in the development
  entitlements file, `production` in the App Store file. Enabling the capability on App ID
  `com.winomail.macos` invalidates every provisioning profile; recreate them with their exact names.
- IMAP and DAV registrations upload credentials after the device token is issued. Gmail and Graph
  builders as in S3. Background refresh (`BGTaskScheduler`) runs `CheckInAsync` for renewals.
- Notification Service Extension is a separate later phase (payload enrichment); the alert text from the
  relay is shown as delivered until then.

## Android

- No picker, no dialog. `SyncMethod` is read as `Relay`.
- `IDeviceAttestationProvider` with Play Integrity (`com.google.android.play:integrity`, standard
  request, token provider prepared at startup). Debug builds signed with the debug key return
  `UNRECOGNIZED_VERSION`; they fall back to the Wino Account path like the Simulator.
- FCM token provider stops being compile-only: `google-services.json` ships with the Play build.
- `WinoMessagingService` already routes pushes to the handler; add `mailbox.renew` handling and a
  WorkManager periodic job calling `CheckInAsync` for renewals.

## Phases

1. S1, S2 (device path, attestation abstraction with `None` only), relay registration with API device
   id, sign-in link and sign-out unlink. Desktop builds green.
2. S3 Gmail and Graph builders, `mailbox.renew`, desktop socket start, fallback timer, indicator (W2 and
   macOS). IMAP IDLE re-enabled.
3. W1 introduction window and policy dialog, S4 version, S5 settings rework. Windows Release gate.
4. iOS App Attest and Android Play Integrity providers, mobile credential registration, renew jobs.
5. Wino Account page device and mailbox lists. Notification Service Extension later.

## Verification

- Windows Debug x64 build and the Release gate; manual checks of the introduction window on an install
  with accounts and a `SyncMethod` key removed from preferences, both outcomes and the decline path.
- macOS native Debug arm64 and x64; iOS Simulator (expect the sign-in fallback) and a device build for
  App Attest; Android emulator (expect the sign-in fallback) and a Play internal-testing build.
- Live relay test with `tools/Wino.Relay.Console` once the relay accepts device tokens.

## Decisions (closed 2026-10-17)

- **D1.** Fresh installs never see the policy dialog. The dialog exists only for installs whose recorded
  policy version is unset or older than the current one (today: existing Windows users).
- **D2.** IMAP IDLE comes back. It runs in Relay mode only, so the timer card stays truthful.
- **D3.** Desktop IMAP is always local. The relay keeps rejecting desktop IMAP registrations.

## Implementation status (2026-10-17, evening)

Done in this repository, uncommitted, shared projects building on the Mac (`Wino.Core`, `Wino.Services`,
`Wino.Mail.ViewModels`, `Wino.PushRelay.Socket`):

- S1: `SyncMethod` preference and availability, `IRelayHealth`/`IRelaySocketCoordinator`, runtime fallback loops
  (mail skips ticks while the socket is up; 5 minute interval in relay mode), IDLE back in relay mode only (D2).
- S2: `IDeviceAttestationProvider`, `IPushRelayDeviceCredentialStore` (Keychain or protected file), API device calls
  on `IWinoAccountApiClient` with the API agent's headers (`X-Relay-Device`, `-Credential`, `-Nonce`, `-Assertion`),
  device-first `PushRelayTokenProvider`, registration service linking on sign-in and unlinking on sign-out,
  `PushRelayOptIn` capability removed, IMAP builder mobile-only (D3).
- S3: Gmail `users.watch` and Graph subscription clients, both builders in `Wino.Core/PushRelay`, app-managed
  renewal through `mailbox.renew` and the stored expiry, re-POST instead of credential update for app-managed rows.
- S4: `IRelayIntroductionService` with the policy version, fresh-install default and the existing-user decision run from
  `ApplicationRuntimeInitialization` on every platform.
- S5 (partial): Windows settings toggle reads the sync method, shows the policy dialog once, wording without credentials.
- W1: `RelayIntroductionWindow`/`RelayIntroductionPage`, `RelayPolicyDialog` through the dialog service, shell hook.
- W2: `RelaySocketCoordinator` starts the socket from the runtime, red dot plus flyout in `WinoAppShell`, Mac toolbar dot.
- iOS `IOSAppAttestProvider` and entitlements; Android `AndroidPlayIntegrityProvider` (classic request) and package.

Not done or not verified:

- Windows head build, XAML formatting (`format-xaml.ps1 -Changed`), and every manual check: nothing here was launched.
- Account pages' status-only rows and the Wino Account page device list (S5 rest, phase 5).
- Android binding names in `AndroidPlayIntegrityProvider` were written from the Play Integrity API shape and need the
  Android build to confirm; iOS `DCAppAttestService` usage likewise needs the iOS build.
- Live test against the deployed relay and API once both backends are deployed (relay first, then API).
- Notification Service Extension, DAV registration on mobile, calendar and contacts Graph subscriptions.

## Verified live (2026-10-17, night)

- iPhone 15 Pro, Debug and Release device builds signed with the regenerated profiles: App Attest registration at the
  API, relay device under the same id, Graph subscription with immutable ids, `mailbox.changed` push, Notification
  Service Extension (`src/Wino.Mail.iOS.NotificationService`) rewriting the alert with sender, subject, preview and
  avatar (shared contact picture from the App Group `group.com.winomail.macos`, then Gravatar, then favicon), the app's
  own notification suppressed, and a cold-launch tap opening the message (provider id, then RFC Message-ID fallback).
- Mac Debug build: existing accounts start on timer, `relay on` switches to relay-based sync, the device links to the
  Wino Account, the Graph subscription is created, the socket connects and reconnects on relaunch, a mail arrives as a
  socket event followed by an inbox sync and a notification. Desktop IMAP stays local.
- Not yet exercised: the fallback red dot (needs the relay unreachable), Windows end to end, Android.

