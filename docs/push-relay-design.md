# Push relay: product and client design

Decisions from the design sessions of 2026-10-16 and 2026-10-17. This document is the single
reference for how live mailbox updates reach each platform, what the user sees, and what the
client repository still has to build. The relay server itself lives in the Wino-Mail-Relay
repository; its API contract is summarized here only where the client depends on it.

Diagrams: [delivery overview](push-relay/overview.svg) and [new-user flow](push-relay/new-user-flow.svg).
Implementation plans: [application](push-relay/plan-app.md), [Wino Mail API](push-relay/plan-api.md),
[WinoRelay](push-relay/plan-relay.md).

## Goal

Mail arrives and the user sees it immediately on every platform. On phones the app wakes from a
push and syncs; on desktops the app receives a socket event and syncs the changed folder. The
relay never reads message bodies. Delta sync remains the source of truth; the relay only says
"something changed here".

## Principles

- **No account requirement.** Every install has its own attested device identity and can use the
  relay from the first launch. A Wino Account is optional and only adds web management.
- **One decision per platform, app-wide.** Desktop users choose a sync method once, for all
  accounts. Mobile has no choice to make. There is no per-account relay switch anywhere.
- **One privacy acceptance.** Choosing relay-based sync shows the privacy policy once per policy
  version. Nothing else asks the user anything.
- **Silent when healthy.** The only visible health signal is a red dot when the relay is down.

## Identity and the Wino Account

The device is the primary identity on the relay. The Wino Account is a label on top of it.

- On first use the app proves it is the genuine Wino app on a real device: App Attest on iOS and
  macOS, Play Integrity on Android. The Wino API verifies the attestation against a nonce it
  issued seconds earlier and mints a relay token with a device subject. Windows and macOS DMG
  builds register Gmail and Outlook only, which verify themselves against Google and Microsoft and
  carry no secret, so they need no attestation.
- Attestation unavailable (Simulator, rooted device, sideloaded build): the app falls back to
  asking for a Wino Account sign-in before registering IMAP or DAV.
- Signing in to a Wino Account sets the owner on this device and everything it registered.
  Signing in on another device links that one too. A device already owned by a different account
  moves to the new one. Signing out clears the owner but keeps the device working.
- The mailbox limit is **ten** distinct mailboxes, per device before sign-in and per owner after.
  The same mailbox on several devices counts once. Linking never reduces what a device already has.
- Deleting the Wino Account deletes every device and mailbox under that owner, including stored
  IMAP credentials. Those devices re-register as anonymous devices on their next launch, so the
  user keeps notifications unless they also uninstall. The deletion page says so.
- Only linked devices appear in the web portal. An anonymous device has no web view; stored
  credentials are removed by removing the account in the app or uninstalling.

### Abuse controls (relay side)

Attestation stops scripted abuse. For a real device the relay also pauses a mailbox after a few
consecutive login failures until the app re-sends credentials, strikes a device that registers
many distinct IMAP hosts or many failing mailboxes in a short window and refuses new registrations
from it for a while, and drops a mailbox after a grace period once its push token is unseen, so
uninstalling also removes stored credentials. Socket and connect-rate caps become per device
subject; the per-IP cap stays.

## Sync method per platform

### Desktop (Windows, macOS)

One app-wide setting, **Sync method**: *Timer-based* or *Relay-based*.

- *Timer-based* keeps today's behavior and the user's configured intervals.
- *Relay-based* keeps a SignalR socket to the relay while the app runs (foreground, tray or dock).
  Mail and calendar fall back to a preconfigured 5 minute timer when the socket is down. Contacts
  and To Do always keep their timer-based approach.
- IMAP on desktop is never sent to the relay (the relay refuses it). The app's own IDLE client
  keeps watching it. No user-facing text points this out.
- Gmail and Outlook: the app registers the Gmail watch (`users.watch`) and the Graph subscription
  with its own token, pointing at the relay's Pub/Sub topic and webhook from `/.well-known/winorelay`.
  The relay stores no OAuth token and learns only "mailbox changed".
- Health: no indicator while connected. When the socket is down and the timer fallback is active,
  the shell shows a small red dot with a flyout: the relay is down and notifications may be
  degraded. Settings carries the full state.
- Users who set Windows "close" to fully exit get no notifications, same as today.

### Mobile (iOS, iPadOS, Android)

There is no timer-based mode. Everything goes through the relay.

- IMAP: the phone uploads server, user name and password after the privacy acceptance. The relay
  watches the mailbox with IDLE and fans out to every registered device. It reads the envelope only
  (sender, subject), never the body or a preview.
- Gmail and Outlook: same app-side watch registration as desktop.
- Declining the privacy policy: the app shows that it will only sync once launched. Settings keeps
  a way back in.
- To Do syncs every couple of hours; the due time is checked when the app launches. Nothing in the
  UI calls this out.

## Privacy acceptance

- Later decision (2026-10-17, evening): fresh installs never see an acceptance dialog; the updated
  policy on the store listing and website covers them, and the first launch records the current
  policy version. The dialog exists for installs whose recorded version is unset or older, which today
  means existing Windows users. The mobile decline notice below is therefore not built. See
  [plan-app.md](push-relay/plan-app.md), decision D1.
- Selecting relay-based sync in any application, for any provider, opens the privacy policy to
  agree to. On desktop, declining returns to the sync method selection. On mobile, declining shows
  the launch-only sync notice.
- The acceptance screen itself states that IMAP server, user name and password are stored
  encrypted on Wino servers and that removal happens by removing the account or uninstalling, then
  links the full policy. Store review requires the disclosure at the point of collection.
- The accepted policy version is stored; a version bump re-prompts once.
- One privacy policy covers all four applications. The macOS, iOS and Android apps are unreleased
  and ship with the updated policy, so fresh installs never see a separate acceptance when adding
  accounts beyond the sync-method flow above.

## Existing Windows users

- The update keeps them on timer-based sync. Nothing changes by default.
- A `TeachingTip` introduces the relay feature once, lets them accept the new privacy policy and
  switch all existing accounts to relay-based sync in one step. Declining leaves timer-based sync.
- The privacy policy link on the Store page and the website is updated with the release.

## Settings surface

- Settings > Notifications hosts the sync method picker (desktop only), one status line, the
  mailbox count, the test notification button and the Wino Account link to the web portal.
- Account pages show relay state per account as status only (on, needs password, not available).
  There is no per-account switch.
- The Wino Account page lists active mailboxes and linked devices when signed in.

## Payloads and delivery

- Every event carries account id, folder id and message id so taps open the right mail and the
  next full sync does not notify twice. Desktop keeps a per-message "relay already notified" mark
  because timer and IDLE still run there.
- iOS: APNs alert push with `mutable-content: 1` and `content-available: 1`. A Notification Service
  Extension enriches Gmail and Outlook alerts with one metadata call using the shared Keychain
  access group; on failure the generic text stays. The extension never writes to the main database.
- Android: FCM high-priority data message. The receiver posts the notification, enriches Gmail and
  Outlook with one metadata call, then schedules a WorkManager delta sync. Force-stopped apps
  receive nothing.
- Visible content: IMAP pushes carry sender and subject. Gmail and Outlook show "New mail in
  {account}" at once, upgraded to sender and subject within seconds. The notification content
  preference applies on every path.
- Gmail watches and Graph subscriptions expire after about a week and only the app can renew
  them. Background refresh (iOS), periodic work (Android) and the hourly check-in (desktop) renew
  them; a phone unopened for a week goes quiet for OAuth accounts until next launch.
- Dismiss events (read, deleted, moved) are rare silent events for messages the relay previously
  notified. Best effort.

## Client work remaining

- Gmail and Graph `IPushRelayMailboxRegistrationBuilder` implementations with renewal. Verify the
  Gmail scope Wino requests allows `users.watch` and that the Windows Gmail client id is on the
  relay's allow-list. The client itself is never touched.
- Attestation: App Attest and Play Integrity providers, the API device-token call, and the Wino
  Account fallback when attestation is unavailable.
- Device-first identity in `PushRelayRegistrationService`: register without a Wino Account, link on
  sign-in, keep working on sign-out.
- Desktop: start the socket from the apps when relay-based sync is selected, 5 minute fallback
  timer, the red dot and flyout, HTTP replay of unacked events on connect.
- Settings: sync method picker, privacy acceptance screen with version, status-only account rows,
  the Windows `TeachingTip` migration.
- iOS Notification Service Extension project with shared Keychain and app group; Android receiver.

## Store and developer console setup for attestation

Verified against Apple and Google documentation on 2026-10-17. None of the apps is published; the
Play Console app does not exist yet; the only Google Cloud project is the one used for Gmail sync.

### Apple (iOS and iPadOS)

- App Attest needs the App Attest capability on App ID `com.winomail.macos` in the developer portal.
  Changing App ID capabilities invalidates every provisioning profile; recreate "Wino Mail iOS
  Development", "Wino Mail iOS App Store", "Wino Mail macOS App Store", "Wino Mail macOS Developer ID"
  and "Wino Mail macOS Development" with their exact names.
- Entitlement `com.apple.developer.devicecheck.appattest-environment`: `development` for development
  signed builds, `production` for TestFlight and App Store. The API verifies against the matching
  `aaguid`.
- No App Store Connect setting and no Apple server credential is required. The App Attest root CA is
  public. App Attest does not run in the Simulator and `DCAppAttestService.isSupported` is false on
  macOS, so macOS never attests; it registers only Gmail and Graph, which verify themselves.
- The App Store privacy label must declare the IMAP credential collection; that belongs to the
  privacy session.

### Google (Android)

- Create the Play Console app for `com.winomail.android` and upload at least an internal-testing
  build. `appRecognitionVerdict = PLAY_RECOGNIZED` is returned only for builds whose package and
  signing certificate match what Play distributes, so sideloaded debug builds get
  `UNRECOGNIZED_VERSION` and fall back to the Wino Account path.
- In Play Console, Release > App integrity > Play Integrity API > Link a Cloud project. Reusing the
  existing Gmail Cloud project is fine and keeps one project; linking enables the API automatically.
- Create a service account in that project with the Play Integrity API role and store its JSON in the
  API's Key Vault. The API calls `decodeIntegrityToken` with it (Google-managed keys; no key download).
- Default quota is 10,000 token requests and 10,000 decryptions per day; an increase needs the app on
  Google Play and the linked project. One attestation per device lifetime plus occasional re-attests
  fits inside that for a long time.
- The Play Data Safety form must declare account credentials collected and linked to the user; that
  belongs to the privacy session.

### Windows and macOS

- No attestation exists. Desktop devices register with attestation kind `none`, can hold only Gmail and
  Graph registrations, and are rate limited per IP at creation.

## Status

- Done: shared `PushRelayRegistrationService`, IMAP builder, Apple and Android push token
  providers, desktop socket client (`src/Wino.PushRelay.Socket`, verified live 2026-10-17).
- Planned: see the three implementation plans linked at the top. Deploy order is relay, then API, then
  apps.
