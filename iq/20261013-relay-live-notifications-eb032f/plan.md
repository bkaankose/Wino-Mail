# Wino Mail: live notifications through WinoRelay

## Context

`D:\Wino-Mail-Relay` (`https://relay.winomail.app`) watches mailboxes and sends data-only APNs/FCM pushes. Nothing in `D:\Wino-Mail` talks to it yet: iOS calls `RegisterForRemoteNotifications()` and only logs the token. This plan adds a shared registration layer plus thin platform adapters so each head registers a device for the signed-in Wino Account, keeps the push token and account list in sync, uploads IMAP credentials for watched mailboxes, and turns incoming pushes into an inbox sync.

Updated on 13 Oct 2026 after relay `7f217d5` and API `0b42546`. Those commits replaced `X-Api-Key` and device secrets with a short-lived ES256 **relay token** minted by the Wino Mail API, and made every relay device belong to a Wino user.

User decisions:
- Scope: device registration + IMAP mailboxes. Gmail `users.watch` / Graph subscriptions come later through provider builders.
- Incoming pushes are handled (iOS `DidReceiveRemoteNotification`, Mac `ReceivedRemoteNotification`) and trigger an InboxOnly sync for the account in `cid`. The phone "foreground + push-triggered" sync-mode change is a separate task.
- All platforms. iOS: always on when signed in. macOS and Windows: opt-in toggle in Settings, off by default, because personal data goes through Wino servers. Windows registers without a push token.
- Live notifications are a Wino Account feature. The relay now enforces this server-side; the client gates on a signed-in Wino Account too.
- No relay or API code changes in this plan. Cross-repo items are listed as a handoff.

## Contract the client targets (verified on latest main of both repos)

### Wino Mail API (`D:\Wino-Mail-API\docs\relay.md`)
- `POST /api/v1/relay/token`, bearer = normal Wino access token. Returns `ApiEnvelope<RelayTokenDto>` with `{ token, expiresUtc, relayUrl }`. Token lifetime is 30 minutes, audience `winorelay`, `sub` = Wino user id, `wr_mbx` = mailbox limit (10).
- `RelayTokenDto` is **not** in `Wino.Mail.Contracts` (package unchanged). The client defines its own DTO.
- Without an ECDSA key the API answers 503 `RELAY_UNAVAILABLE`. Banned or suspended users are rejected like every other API call.
- On account deletion, ban or suspension, the API tells the relay itself (`/internal/v1/users/...`). The client does nothing for those cases beyond handling `device_revoked`.

### Relay (`D:\Wino-Mail-Relay\README.md`, `src/WinoRelay.Api/Contracts/Contracts.cs`, `Auth/Filters.cs`)
- Every `/v1` call: `Authorization: Bearer <relay token>`. Device-scoped calls (`/v1/devices/me*`, `/v1/mailboxes*`) also send `X-Device-Id: <deviceId>`. There is no `X-Api-Key` and no device secret anymore.
- 401 problem details carry `reason`: `auth_required`, `auth_invalid`, `token_expired` (get a new relay token) or `device_revoked` (register again and re-add mailboxes). 503 `auth_unavailable` when the relay cannot load signing keys. 400 `reason = device_id_required` when the header is missing.
- `POST /v1/devices {platform ios|macos|android, pushToken?, appVersion?, appBuild?, locale?, osVersion?, name?}` -> 201 `{deviceId, serverTime}`. One device holds one Wino Account; register after every sign-in. Rate limit 10 per user per minute. `windows` is still rejected with 400 `invalid_request`.
- `GET|PATCH|DELETE /v1/devices/me` (PATCH also takes `osVersion`, `name`). `DELETE` is sign-out and must run **before** the app discards its Wino tokens. `POST /v1/devices/me/push-test` (409 without a token). `PUT /v1/devices/me/accounts {clientAccountIds[]}` (full list) -> `{removed, mailboxes[]}`.
- `POST /v1/mailboxes {provider:"imap", clientAccountId, imap:{host, port, security ssl|starttls|none, username, password, authMethod:"password"}}` -> 201 / 200 / 409 `mailbox_already_added` (+`subscriptionId`) / **422 `mailbox_limit_reached`** (10 distinct mailboxes per Wino Account, the same mailbox on several devices counts once). `PUT /v1/mailboxes/{subscriptionId}/credentials`, `DELETE`, `POST .../resync`, `GET /v1/mailboxes`. `MailboxResponse` gained `muted`.
- `/v1/me/*` is the account portal (pause, mute, rename, remove, push-test across devices), used server-side by the website. The app does not need it in this pass.
- Pause and mute silence only `mailbox.changed`. Resume or unmute sends one `mailbox.changed` with `r = resync`.
- Rate limits: 300 per user per minute, 120 per device per minute, 6 resyncs per mailbox per hour, 1 push-test per minute.
- Lifecycle: devices are revoked on sign-out, portal removal, ban, suspension or deletion, and by the reaper (30 days without a call, 3 days with no subscriptions, 7 days after a dead token). A push token registered by another Wino Account's device revokes the old device at once. The app checks in daily.
- Push payload unchanged: data-only strings `v, t (mailbox.changed|mailbox.status|mailbox.reauth|mailbox.error|mailbox.renew|test), sid, cid, mid, p, r, ts, f, uv, un, hm, ex, hid, n, mr, mm, rt, exp`. One APNs bundle id `com.winomail.macos` for iOS and macOS.

## Verified app facts the design relies on

- Startup hook for all heads: `ApplicationRuntimeInitialization.RunBackgroundStartupAsync` ([src/Wino.Core/Services/ApplicationRuntimeInitialization.cs:64](src/Wino.Core/Services/ApplicationRuntimeInitialization.cs:64)), fire-and-forget from `ApplicationRuntime.InitializeAsync`.
- Wino API client: `WinoAccountApiClient.SendAuthorizedRequestAsync(HttpMethod.Post, route, typeInfo, ct)` already handles bearer + refresh-on-401 (pattern: `CreateStoreCollectionsIdTicketAsync`, [src/Wino.Services/WinoAccountApiClient.cs:241](src/Wino.Services/WinoAccountApiClient.cs:241)). Its JSON context is `WinoAccountApiJsonContext` in the same file.
- Sign-out order: `WinoAccountProfileService.SignOutAsync` ([src/Wino.Services/WinoAccountProfileService.cs:309](src/Wino.Services/WinoAccountProfileService.cs:309)) calls `_sessions.ReplaceAsync(null, ...)`, which deletes the Wino tokens, **then** sends `WinoAccountSignedOutMessage`. The relay `DELETE /v1/devices/me` therefore has to run inside `SignOutAsync` before `ReplaceAsync`, not from the message.
- Signed-in check without network: `IWinoAccountSessionService.CaptureAsync` (returns `AccountId`, the Wino user id) or `IWinoAccountProfileService.HasActiveAccountAsync`. The relay service uses the session service so it does not depend on the profile service (which will depend on it for sign-out).
- Account messages: `AccountCreatedMessage` (from `AccountSetupProgressPageViewModel`), `AccountRemovedMessage` (`AccountService.DeleteAccountAsync`), `AccountUpdatedMessage` (`UpdateAccountAsync`, `UpdateImapConnectionSettingsAsync`). `WinoAccountSignedInMessage` after login.
- IMAP data: `IAccountService.GetAccountCustomServerInformationAsync(accountId)` returns hydrated secrets ([src/Wino.Services/AccountService.cs:675](src/Wino.Services/AccountService.cs:675)). Fields: `IncomingServer`, `IncomingServerPort` (string), `IncomingServerUsername`, `IncomingServerPassword`, `IncomingServerType`, `IncomingServerSocketOption` (`Auto|None|StartTls|SslTls`). Wino never does OAuth over IMAP, so `authMethod` is always `password`.
- Per-device state goes in `IConfigurationService` (non-roaming), never in `IPreferencesService`: `src/Wino.SourceGenerators/Preferences/PreferencesPropertyGenerator.cs` exports every preference except `DiagnosticId` into profile sync.
- `AppleKeychainStore` never sets `SecRecord.Accessible`; the device id should be stored "this device only" so an iCloud or encrypted-backup restore onto another phone does not reuse it.
- `Wino.Core.Domain` already references `HttpRequestMessage` (`IDavTransport`).
- Capabilities constructed in `src/Wino.Platform.iOS/IOSPlatformRegistration.cs`, `src/Wino.Mail.MacOS/Composition.cs`, `src/Wino.Mail.WinUI/CoreUWPContainerSetup.cs`.
- Settings: shared VM `src/Wino.Mail.ViewModels/Common/NotificationSettingsPageViewModel.cs` (namespace `Wino.Core.ViewModels`; the copy under `src/Wino.Core.ViewModels/` is legacy). Pages: `src/Wino.Mail.WinUI/Views/Settings/NotificationSettingsPage.xaml`, `src/Wino.Mail.MacOS/Views/Settings/NotificationSettingsPageViewController.cs`, `src/Wino.Mail.UIKit/Views/Settings/NotificationSettingsPageViewController.cs`.
- Mac `AppDelegate` uses partials with `partial void XxxServicesReady()` hooks declared in `AppDelegate.cs`. Mac entitlements lack `aps-environment`.
- iOS `SceneDelegate` has `sceneDidEnterBackground:` only. `BackgroundRefreshLifecycleHook.Run` calls `IApplicationRuntime.SynchronizeOnceAsync`.
- `IAppMetadataService.AppVersion`; build via `SentryDist` (CFBundleVersion on Apple; verify Windows, else null). Locale: `ITranslationService.CurrentLanguageModel.Code`, fallback `CultureInfo.CurrentUICulture.Name`.
- Tests stub `HttpMessageHandler` (`tests/Wino.Core.Tests/Services/WinoAccountApiClientFailureTests.cs`).

## Architecture

```
triggers -> PushRelayRegistrationService (shared; single worker, coalesced, persisted backoff)
             gate     : Wino session present && (mandatory || opted in)
             token    -> IPushRelayTokenProvider -> IWinoAccountApiClient.CreateRelayTokenAsync (POST /api/v1/relay/token)
             device   -> IPushRelayDeviceIdentityStore (platform)   POST /v1/devices
             info     -> IPushRelayDeviceInfo (platform: platform, name, OS version)
             push     -> IPushTokenProvider (platform)              PATCH /v1/devices/me
             accounts -> IAccountService                            PUT /v1/devices/me/accounts
             mailboxes-> IPushRelayMailboxRegistrationBuilder[]     POST/PUT /v1/mailboxes
             state    -> IPushRelayStateStore (IConfigurationService JSON)
             http     -> IPushRelayClient (Bearer relay token, X-Device-Id)
sign-out -> WinoAccountProfileService.SignOutAsync -> registration.SignOutAsync (DELETE /v1/devices/me) -> tokens discarded
push payload -> PushRelayNotificationHandler -> IApplicationRuntime.SynchronizeAccountInboxAsync(cid)
```

Platform heads only: store the device id, describe the device, acquire and forward the push token, forward pushes, render the settings card.

## 1. Domain (`src/Wino.Core.Domain`)

- `Models/PushRelay/PushRelayDefaults.cs`: `DefaultBaseUrl = https://relay.winomail.app/`. The relay token response's `relayUrl` wins when present. No API key.
- `Models/PushRelay/RelayTokenResult.cs`: `RelayTokenDto(string Token, DateTimeOffset ExpiresUtc, string RelayUrl)` mirroring the API record, plus `RelayApiErrorCodes.RelayUnavailable = "RELAY_UNAVAILABLE"`.
- `Models/PushRelay/` DTO records mirroring relay contracts, collections as `List<T>`: `PushRelayRegisterDeviceRequest(Platform, PushToken?, AppVersion?, AppBuild?, Locale?, OsVersion?, Name?)`, `PushRelayRegisterDeviceResponse(Guid DeviceId, DateTimeOffset ServerTime)`, `PushRelayUpdateDeviceRequest(PushToken?, AppVersion?, AppBuild?, Locale?, OsVersion?, Name?)`, `PushRelayDevice(..., Name?, OsVersion?)`, `PushRelaySyncAccountsRequest/Response`, `PushRelayImapCredentials`, `PushRelayCreateMailboxRequest(Provider, ClientAccountId, Imap?)` (Gmail/Graph added later), `PushRelayUpdateCredentialsRequest`, `PushRelayMailbox(..., RegistrationExpiresAt?, bool Muted)`, `PushRelayMonitoring`, `PushRelayHints`, `PushRelayMailboxList`, `PushRelayResyncResponse`, `PushRelayPushTestResponse`, `PushRelayWellKnown`.
- `PushRelayProblem(Code, Status, Title?, Detail?, Reason?, RetryAfterSeconds?, SubscriptionId?)`; `PushRelayErrorCodes` (relay codes incl. `mailbox_limit_reached`, `auth_unavailable`, plus client-side `service_unavailable`, `invalid_response`); `PushRelayAuthReasons` (`auth_required`, `auth_invalid`, `token_expired`, `device_revoked`, `device_id_required`); `PushRelayPushKind` constants.
- `PushRelayDeviceIdentity(Guid DeviceId, Guid WinoUserId, string Platform, DateTime RegisteredAtUtc)`. The Wino user id lets the service notice an account switch and re-register without a failing call.
- `PushRelayDeviceDescriptor(string Platform, string? Name, string? OsVersion)`.
- `PushRelayState` (persisted): `UserOptIn`, `LastCheckInUtc`, `NextAttemptUtc`, `FailureCount`, `LastErrorCode/Detail`, `LastPushTokenHash`, `LastAppVersion`, `LastLocale`, `LastName`, `LastOsVersion`, `PlatformRejected` + `PlatformRejectedAppVersion`, `LastTestPushUtc`, `MailboxLimit`, `Dictionary<Guid, PushRelayMailboxRegistration> Mailboxes` (`SubscriptionId`, `CredentialFingerprint`, `Status`, `Muted`, `MonitoringReason/Message`, `LastErrorCode`, `UpdatedUtc`).
- Enums: `PushRelaySyncReason` (Startup, AccountsChanged, CredentialsChanged, PushTokenChanged, WinoAccountChanged, OptInChanged, Foreground, CheckIn, DeviceRevoked, UserRequested), `PushRelayAvailability` (UnsupportedPlatform, SignInRequired, DisabledByUser, Enabled, RejectedByRelay, ServiceUnavailable), `PushRelayNotificationOutcome` (Ignored, NoData, NewData, Failed). `PushRelayRegistrationSnapshot` record for the UI (availability, device id, has push token, last check-in, next attempt, last error, mailbox entries, mailbox limit).
- `Exceptions/PushRelayApiException`: wraps `PushRelayProblem`; `IsTokenRejected` (401 with `token_expired`/`auth_invalid`/`auth_required`), `IsDeviceRevoked` (401 `device_revoked`), `IsRateLimited`, `IsTransient` (5xx, `auth_unavailable`, client-side codes).
- Interfaces:
  - `IWinoAccountApiClient.CreateRelayTokenAsync(ct)` -> `ApiEnvelope<RelayTokenDto>` (new member; implemented in `WinoAccountApiClient` with `SendAuthorizedRequestAsync(HttpMethod.Post, "api/v1/relay/token", ...)`; add `ApiEnvelope<RelayTokenDto>` to `WinoAccountApiJsonContext`).
  - `IPushRelayTokenProvider`: `GetTokenAsync(ct)` (cached until `expiresUtc - 2 min`, single-flight), `Invalidate()`. Shared implementation; this is the only place that talks to the Wino API for the relay.
  - `IPushRelayClient`: `GetWellKnownAsync`, `RegisterDeviceAsync(req)`, `GetDeviceAsync(deviceId)`, `UpdateDeviceAsync(deviceId, req)`, `DeleteDeviceAsync(deviceId)`, `SendPushTestAsync(deviceId)`, `SyncAccountsAsync(deviceId, ids)`, `CreateMailboxAsync(deviceId, req)`, `UpdateMailboxCredentialsAsync(deviceId, subscriptionId, req)`, `DeleteMailboxAsync`, `ResyncMailboxAsync`, `ListMailboxesAsync`. The client gets the bearer from `IPushRelayTokenProvider`; on 401 `token_expired`/`auth_invalid` it invalidates the token and retries once. `device_revoked` is surfaced to the orchestrator.
  - `IPushRelayDeviceIdentityStore` (platform): `LoadAsync`, `SaveAsync`, `DeleteAsync`.
  - `IPushRelayDeviceInfo` (platform): `PushRelayDeviceDescriptor Describe()`.
  - `IPushTokenProvider` (platform): `IsPushChannelAvailable`, `CurrentToken` (lowercase hex), `LastFailure`, `event TokenChanged`.
  - `IPushRelayStateStore`: `Load`, `Save`, `Clear` (keeps opt-in).
  - `IPushRelayMailboxRegistrationBuilder`: `ProviderType`, `IsEligibleAsync(account)`, `BuildAsync(account)` -> `(Create, Update, CredentialFingerprint)` or null.
  - `IPushRelayRegistrationService`: `Snapshot`, `SnapshotChanged`, `IsUserOptedIn`, `StartAsync`, `RequestSync(reason)`, `SyncNowAsync(reason)`, `SetUserOptInAsync(bool)`, `CheckInAsync`, `SignOutAsync(ct)` (bounded, best effort), `SendPushTestAsync`, `ReportMailboxStatus(...)`, `ReportTestPush()`.
  - `IPushRelayNotificationHandler`: `HandleAsync(IReadOnlyDictionary<string,string> payload, ct)`.
  - `IApplicationRuntime.SynchronizeAccountInboxAsync(Guid accountId, ct)`: one bounded InboxOnly pass for one account, awaitable. Implement in `src/Wino.Core/Services/ApplicationRuntime.cs` next to `SynchronizeOnceAsync`, skipping accounts without mail access.
  - `IPlatformCapabilities` / `PlatformCapabilities`: add `RemoteNotifications` and `PushRelayOptIn`. iOS `true/false`, Mac `true/true`, Windows `false/true`.

## 2. Shared services (`src/Wino.Services/PushRelay/`)

- `PushRelayJsonContext`: source-generated, camelCase, ignore nulls, case-insensitive; every relay DTO, `PushRelayState`, `PushRelayDeviceIdentity`, and `Dictionary<string, JsonElement>` for problems. No reflection serialization (Native AOT).
- `PushRelayTokenProvider : IPushRelayTokenProvider`: calls `IWinoAccountApiClient.CreateRelayTokenAsync`. Failure mapping: envelope error `RELAY_UNAVAILABLE` or service failure -> `PushRelayApiException(service_unavailable)`; sign-in required -> `SignInRequired` availability. Caches `RelayUrl` for the client's base address. Never logs the token.
- `PushRelayClient : IPushRelayClient, IDisposable`: ctor `(IPushRelayTokenProvider, HttpClient? = null)`. Sets `Authorization: Bearer`, `X-Device-Id` on device-scoped routes, `Accept: application/json`. Base address from the token's `relayUrl`, else `PushRelayDefaults.DefaultBaseUrl`. Maps `HttpRequestException`/`IOException`/timeouts to `service_unavailable`; non-JSON error bodies to `service_unavailable` (5xx) or `invalid_response`; `application/problem+json` to `PushRelayApiException` (strip `urn:winorelay:error:`, read `reason`, `retryAfterSeconds`, `subscriptionId`). One automatic retry after `token_expired`/`auth_invalid`. Never logs request bodies or tokens.
- `ConfigurationPushRelayStateStore`: keys `PushRelay.OptIn` (bool) and `PushRelay.State` (JSON), non-roaming.
- `ImapPushRelayMailboxRegistrationBuilder`: eligible when `ProviderType == IMAP4`, `IsMailAccessGranted`, server info is IMAP4 with host/username/password, `AttentionReason != InvalidCredentials`. Mapping `SslTls->ssl`, `StartTls->starttls`, `None->none`, `Auto -> port==993 ? ssl : starttls`; port parse with fallback 993/143; username fallback `MailAccount.Address`; `clientAccountId = account.Id.ToString("D")`. Fingerprint = SHA-256 hex of `host\0port\0security\0username\0password`.
- `PushRelayRegistrationService`: recipient of account and Wino sign-in messages; subscribes `IPushTokenProvider.TokenChanged`; `PeriodicTimer(1h)` requests `CheckIn`. Single worker (`SemaphoreSlim(1,1)`), coalesced pending reasons, `TimeProvider` injectable. Depends on `IWinoAccountSessionService` (not the profile service) to avoid a DI cycle.
  Pass:
  1. Availability: platform supported; Wino session present (else `SignInRequired`); opt-in on for opt-in platforms; platform not rejected for this app version.
  2. Not enabled and an identity exists -> teardown: best-effort `DeleteDeviceAsync` (10 s) while a Wino session still exists, then delete identity and clear state; publish snapshot.
  3. Honour `NextAttemptUtc` backoff except for `UserRequested`, `OptInChanged`, `DeviceRevoked`.
  4. Identity missing, or `identity.WinoUserId != session.AccountId` -> `RegisterDeviceAsync(descriptor.Platform, pushToken, appVersion, build, locale, osVersion, name)`; save identity first, then state, clear mailbox map. Windows `invalid_request` -> mark `PlatformRejected` for this app version and stop.
  5. Push token hash, app version, locale, name or OS version changed -> `UpdateDeviceAsync`; else if check-in older than 20 h -> `GetDeviceAsync` (check-in, returns server mailbox view).
  6. `SyncAccountsAsync` with all eligible ids; drop local entries missing from the response.
  7. Per eligible account: create when absent (`mailbox_already_added` -> use its `subscriptionId` then PUT credentials); PUT credentials when the fingerprint differs or the server says `needs_reauth`. Record subscription id, fingerprint, status, `muted`, monitoring reason/message.
  8. Reset failures and backoff, stamp `LastCheckInUtc`, save, publish snapshot.
  Error policy:
  - 401 `device_revoked` -> delete identity, clear mailbox map, rerun from step 4 once; a second one -> backoff.
  - Token rejected after the client's own retry, or `SignInRequired` from the token provider -> stop, availability `SignInRequired`.
  - 429 -> `NextAttemptUtc = now + retryAfterSeconds` (default 60 s).
  - Transient (5xx, `auth_unavailable`, `RELAY_UNAVAILABLE`, network) -> backoff `1m, 5m, 15m, 1h, 6h, 24h`, persisted; availability `ServiceUnavailable` after repeated failures.
  - Per-mailbox 422 `mailbox_limit_reached`, `provider_auth_failed`, `unsupported_provider`, 400 -> recorded on that entry only; continue. On `mailbox_limit_reached`, stop creating further mailboxes in this pass.
  - `OperationCanceledException` rethrown.
- `SignOutAsync`: if an identity exists, `DeleteDeviceAsync` with a 5 s budget, then delete identity and clear state regardless of the outcome. Called from `WinoAccountProfileService.SignOutAsync` **before** `_sessions.ReplaceAsync(null, ...)`, wrapped in try/catch so a relay failure never blocks sign-out. Inject `Func<IPushRelayRegistrationService>` into the profile service to keep construction lazy. The later `WinoAccountSignedOutMessage` then finds nothing to tear down.
- `PushRelayNotificationHandler`: parses `t`, `cid`, `mid`, `sid`, `r`, `mr`, `mm`, `rt`. Unknown kind, missing or unknown `cid` -> `Ignored`. `test` -> `ReportTestPush`, `NoData`. `mailbox.changed` (including `r = resync` after unpause/unmute) -> `SynchronizeAccountInboxAsync(cid)` under a 25 s CTS -> `NewData`/`Failed`. `mailbox.reauth` -> report status, clear the local fingerprint, `RequestSync(CredentialsChanged)`, then the inbox sync (a really wrong password is surfaced by the existing `ImapAuthenticationFailedHandler`). `mailbox.status`/`mailbox.error` -> report status. `mailbox.renew` -> logged (Gmail/Graph pass).
- DI in `ServicesContainerSetup.RegisterSharedServices`: singletons for token provider, client, state store, IMAP builder, registration service, notification handler. `ApplicationRuntimeInitialization`: add `Func<IPushRelayRegistrationService>` and call `StartAsync` inside `RunBackgroundStartupAsync` with try/catch; update `CoreContainerSetup` accordingly.

## 3. Shared ViewModel and strings

- `NotificationSettingsPageViewModel`: inject `IPushRelayRegistrationService` and `IPlatformCapabilities`; subscribe to `SnapshotChanged` while on the page. Members: `IsPushRelaySectionVisible`, `IsPushRelayToggleVisible` (opt-in platforms), `IsPushRelayEnabled` (setter -> `SetUserOptInAsync`), `IsPushRelayToggleEnabled`, `IsPushRelaySignInRequired`, `PushRelayStatusText`, `IsPushTestVisible`, `SendPushTestCommand`. `ResetAsync` must not touch the opt-in.
- `src/Wino.Core.Domain/Translations/en_US/resources.json` only, near `NotificationSettings_*`: `NotificationSettings_PushRelay_Title`, `_Description` (IMAP server, user name and password are stored encrypted on Wino servers; requires a Wino Account; mailboxes can be paused from the Wino Account page), `_SignInRequired`, `_Off`, `_RegisteredFormat` ({0} of {1} mailboxes watched), `_ErrorFormat`, `_ServiceUnavailable`, `_PlatformUnsupported`, `_LimitReached`, `_SendTest`, `_TestQueued`, `_TestNoToken`, `_Mailbox_NeedsReauth`.

## 4. Platform adapters

### Apple shared (`src/Wino.Platform.Apple`)
- `Security/AppleKeychainStore.cs`: `Write`/`TryAdd` overloads taking `SecAccessible` (existing overloads unchanged).
- `PushRelay/AppleKeychainPushRelayDeviceIdentityStore`: scope `push-relay`, account `device-identity-v1`, JSON of `PushRelayDeviceIdentity`, `SecAccessible.AfterFirstUnlockThisDeviceOnly` (not restored onto another device, readable in background push handling). Null on not-found or corrupt data.
- `PushRelay/ApplePushTokenProvider`: `SetToken(NSData)` -> lowercase hex, event only on change; `SetFailure(string)`.
- `PushRelay/ApplePushPayload.ToPayload(NSDictionary)`: top-level string values, skips `aps`.

### iOS
- `IOSPlatformRegistration.cs`: register `ApplePushTokenProvider` (self + `IPushTokenProvider`), keychain identity store, `IOSPushRelayDeviceInfo` (`ios`, `UIDevice.CurrentDevice.Name`, `"iOS " + SystemVersion`); capabilities `true/false`.
- `AppDelegate.cs`: forward token and failure; add `DidReceiveRemoteNotification(UIApplication, NSDictionary, Action<UIBackgroundFetchResult>)` that awaits `_startup`, runs the handler under 25 s, maps the outcome and calls the completion handler exactly once. Remove the "separate workstream" comments here, in `Entitlements.plist`, and in `docs/harness/development-ios.md`.
- `LifecycleHooks.cs` `BackgroundRefreshLifecycleHook.Run`: after `SynchronizeOnceAsync`, `CheckInAsync`.
- `SceneDelegate.cs`: `[Export("sceneWillEnterForeground:")]` -> `RequestSync(Foreground)`.
- `src/Wino.Mail.UIKit/Views/Settings/NotificationSettingsPageViewController.cs`: status card, "Send test push" button, sign-in InfoBar; no toggle.

### macOS
- `Composition.cs`: same Apple registrations plus `MacPushRelayDeviceInfo` (`macos`, `NSHost.Current.LocalizedName`, `"macOS " + NSProcessInfo.OperatingSystemVersion`); capabilities `true/true`.
- New `AppDelegate.PushRelay.cs` with `partial void PushRelayServicesReady()` (declared and called in `AppDelegate.cs`): `RegisterForRemoteNotifications()` on the main thread; overrides `RegisteredForRemoteNotifications`, `FailedToRegisterForRemoteNotifications`, `ReceivedRemoteNotification`; `DidBecomeActive` -> `RequestSync(Foreground)`.
- `Entitlements.plist` `aps-environment = development`; `Entitlements.AppStore.plist` `production`. Note in `docs/harness/development-macos.md` that the provisioning profiles need the Push Notifications capability.
- Settings page: expander with switch, description, status and test-push cards.

### Windows
- `src/Wino.Platform.Windows/PushRelay/WindowsPushRelayDeviceIdentityStore`: JSON in a local file under `ApplicationDataFolderPath\PushRelay\device-identity.json`, written atomically (pattern from `WindowsGoogleTokenStore.WriteAsync`). No secret is stored anymore, so DPAPI is not needed; the file stays out of profile export.
- `NullPushTokenProvider` and `WindowsPushRelayDeviceInfo` (`windows`, `Environment.MachineName`, `"Windows " + Environment.OSVersion.Version`).
- `CoreUWPContainerSetup.cs`: register them; capabilities `false/true`.
- `NotificationSettingsPage.xaml`: `SettingsExpander` after Tasks with header `ToggleSwitch`, description, status, sign-in InfoBar; push-test hidden. Run `scripts/maintenance/windows/format-xaml.ps1 -Changed`.

## 5. Tests (`tests/Wino.Core.Tests/Services/PushRelay/`, `tests/Wino.Mail.ViewModels.Tests/`)

Helpers: stub `HttpMessageHandler`, in-memory identity and state stores, fake token provider, fake push token provider, fake `TimeProvider`, Moq for `IAccountService`, `IWinoAccountSessionService`, `IPlatformCapabilities`, `IWinoAccountApiClient`.
- `WinoAccountApiClient` relay-token test: `POST api/v1/relay/token` with bearer, envelope parsed, `RELAY_UNAVAILABLE` surfaced.
- `PushRelayTokenProviderTests`: caching until 2 minutes before expiry, single-flight under concurrency, `Invalidate` forces a new call, base URL taken from `relayUrl`.
- `PushRelayClientTests`: bearer on every route; `X-Device-Id` only on device-scoped routes; camelCase and null omission; problem parsing (`reason`, `retryAfterSeconds`, `subscriptionId`); one retry after `token_expired` with a fresh token, none after `device_revoked`; HTML 502 and network failure -> `service_unavailable`; cancellation rethrows.
- `ImapPushRelayMailboxRegistrationBuilderTests`: security/port mapping, username fallback, ineligibility cases, fingerprint stability.
- `PushRelayRegistrationServiceTests`: no Wino session -> no HTTP; opt-in off -> `DisabledByUser`; first pass order (identity saved before accounts sync); register sends name and OS version; Wino user change -> re-register without a failing call; no-op within 20 h; token, name or OS change -> PATCH; credential change -> PUT; 409 -> PUT on returned id; 422 `mailbox_limit_reached` -> recorded, later accounts not attempted; `device_revoked` -> re-register once then backoff; 429 and 5xx backoff; Windows `invalid_request` -> `RejectedByRelay` until the app version changes; coalescing; opt-out teardown; `SignOutAsync` deletes the device and clears state even when the call fails.
- `WinoAccountProfileService` sign-out test: relay `SignOutAsync` runs before the session is replaced, and a throwing relay call does not block sign-out.
- `PushRelayNotificationHandlerTests`: changed and `r = resync` -> one inbox sync; unknown cid ignored; test -> snapshot; reauth -> status + sync; malformed ignored.
- `ApplicationRuntime` test for `SynchronizeAccountInboxAsync`.
- ViewModel tests: status text per availability, toggle state, iOS hides the toggle.

## 6. Verification

Shared production code touches all three apps (AGENTS.md "Shared production code"):

```powershell
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj --filter "FullyQualifiedName~PushRelay|FullyQualifiedName~WinoAccountApiClient|FullyQualifiedName~WinoAccountProfileService"
dotnet test tests/Wino.Mail.ViewModels.Tests/Wino.Mail.ViewModels.Tests.csproj --filter "FullyQualifiedName~NotificationSettings"
scripts/maintenance/windows/format-xaml.ps1 -Changed ; scripts/maintenance/windows/format-xaml.ps1 -Check
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Mac handoff (over SSH per `docs/harness/development-macos.md`): native Debug arm64 and x64 builds; after the entitlement change also Release arm64/x64 and `codesign -d --entitlements :-` showing `aps-environment`. iOS handoff: Simulator Debug build of `WinoMail.iOS.slnx`, then a signed device build (pushes only arrive on a physical device).

Manual checks (production API must have its ECDSA key so the token endpoint works):
1. Windows: the expander is visible; the toggle is disabled until Wino sign-in. After opt-in, the token call succeeds and the relay rejects `windows`; status shows "platform unsupported" with no retry storm.
2. Mac: toggle on -> token logged -> device listed on the Wino Account page with name and macOS version -> "Send test push" -> `test` payload arrives.
3. iPhone with APNs enabled on the relay: add an IMAP account -> `POST /v1/mailboxes` 201 -> send mail -> `DidReceiveRemoteNotification` -> inbox sync without opening the app. The relay's APNs sandbox flag must match the build's `aps-environment`.
4. Change the IMAP password -> `PUT .../credentials`. Remove the account -> `PUT /v1/devices/me/accounts` reports `removed: 1`.
5. Pause the mailbox on the Wino Account page -> no `mailbox.changed`; resume -> one push with `r = resync` triggers a sync.
6. Remove the device on the Wino Account page -> next pass gets `device_revoked` -> a new device id appears and mailboxes are re-added.
7. Sign out of the Wino Account -> relay logs `DELETE /v1/devices/me` before the API logout, keychain item gone. Sign in as a different Wino user -> a new device registers for that user.
8. Add more than 10 IMAP mailboxes -> the 11th shows "limit reached", the others stay active.
9. iOS Simulator: registration failure logged, device registers with a null push token, test push shows the "no token" text, no crash.

## 7. Handoff to the relay/API session

1. The relay still rejects `platform: "windows"` (`Platforms.IsKnown`). Accept it for devices without a push channel, or confirm Windows should stay unregistered. Until then the client marks the platform rejected per app version.
2. Enable APNs on the relay (p8 key, team id, bundle `com.winomail.macos`) and pick the sandbox/production flag per environment.
3. Confirm the production API has `Secrets:Jwt:EcdsaPrivateKey` and `EcdsaKeyId` set, otherwise `POST /api/v1/relay/token` answers 503 `RELAY_UNAVAILABLE` and the feature stays dormant.
4. Confirm `PUT /v1/devices/me/accounts` ignores ids that were never POSTed (the client sends the full eligible list before creating mailboxes).
5. Decide whether `test` pushes should reach macOS as background pushes and whether an alert push type with a Notification Service Extension is planned for user-visible mail.
6. Optional: add `RelayTokenDto` to `Wino.Mail.Contracts` later; the client keeps a local copy until then.

## 8. Sequencing

1. Domain: DTOs, enums, interfaces, exception, capabilities, `SynchronizeAccountInboxAsync`, `CreateRelayTokenAsync` on the API client interface.
2. Services: JSON context, relay-token call in `WinoAccountApiClient`, token provider, relay client, state store, IMAP builder, registration service, notification handler, DI, startup hook, sign-out hook in `WinoAccountProfileService`.
3. Unit tests for 1 and 2; run before any head work.
4. Translations, ViewModel, ViewModel tests.
5. Windows head -> Debug build + Release gate.
6. Apple shared (keychain overload, identity store, token provider, payload helper).
7. iOS head -> Simulator build handoff.
8. Mac head -> arm64/x64 build handoff.
