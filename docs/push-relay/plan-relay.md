# Push relay: WinoRelay plan (device subjects and owner adoption)

Companion to [push-relay-design.md](../push-relay-design.md). Other plans: [app](plan-app.md),
[API](plan-api.md). Repository: Wino-Mail-Relay. File references verified on 2026-10-17.

## What exists today (verified)

- `WinoTokenValidator` reads `iss`, `aud`, `sub`, `exp`, `nbf`, `iat`, `scope`, `wr_mbx`. `UserAuthFilter`
  requires `Guid.TryParse(sub)` and stores `UserContext(userId, maxMailboxes, scopes)`.
  `DeviceAuthFilter` rejects when `device.UserId != user.UserId`. `SocketAuthenticator` repeats both.
- `devices.user_id` is already nullable; legacy unowned rows were revoked in migration 0004.
  `IDeviceRepository.CreateAsync` takes a non-null user id and generates the device id.
- Per-user limits (`wr_mbx`, `Api:MaxOnlineMailboxesPerUser`, socket caps, connect rate, register rate)
  all key on `device.UserId`. `MailboxService.CreateAsync` skips both mailbox limits when
  `device.UserId` is null.
- `device_connections.user_id` is `NOT NULL`. `user_mailbox_pauses` is keyed by user. Fan-out is per
  `device_subscriptions` row, not per user.
- IMAP auth failures disable the mailbox after 3 failures over 10 minutes or immediately for permanent
  failures (`AccountMonitor`, `FailureClassifier`), status `needs_reauth`. No per-device registration
  strike or ban exists; socket strikes exist per device.
- Internal endpoints: `DELETE /internal/v1/users/{id}`, `POST .../revoke`, `GET .../connections`.
- `tests/WinoRelay.IntegrationTests` predates Wino tokens (uses `X-Api-Key`, `deviceSecret`) and cannot
  start as written.

## R1. Principal model

- `TokenJwtPayload` reads `wr_kind` (`user` | `device`; missing means `user` for compatibility).
- Replace `UserContext` with `PrincipalContext(Kind, SubjectId, MaxMailboxes, Scopes)` and keep
  `UserAuthFilter.Current(http)` returning it. `OwnerKey(Device) => device.UserId ?? device.Id`.
- `DeviceAuthFilter` and `SocketAuthenticator` accept a request when:
  - `Kind == user` and `device.UserId == SubjectId` (today's rule), or
  - `Kind == device` and `device.Id == SubjectId` (owned or not; a linked device may still present a
    device token until the app switches paths).
- `MonitorAuthFilter`, `InternalAuthFilter` unchanged. `/v1/me` requires `Kind == user`, otherwise 403
  `user_token_required`.

## R2. Device registration with a caller-supplied id

- `POST /v1/devices` with `Kind == device`: the device id is the token subject. If the row exists and is
  not revoked, update it and return 200 (idempotent re-registration). If it exists revoked, return 401
  `device_revoked`. `user_id` is null. The per-user register rate limit becomes per owner key.
- `POST /v1/devices` with `Kind == user`: unchanged, except the request may carry `deviceId` (the API
  device id) so a user-path device uses the same id the API knows. When absent, generate as today.
- `IDeviceRepository.CreateAsync(Guid id, Guid? userId, ...)`.
- Migration `0010_device_owner.sql`: `device_connections.user_id` becomes `owner_key uuid NOT NULL`
  (rename plus backfill from `devices`), index renamed. `device_reachable` unchanged. Add
  `devices.owner_changed_at timestamptz`.

## R3. Owner adoption

- `PUT /internal/v1/devices/{id}/owner {userId: uuid|null}`: sets `devices.user_id`, stamps
  `owner_changed_at`, publishes a `SocketDisconnect` with reason `replaced` so a connected socket
  re-authenticates under the new owner key (the client reconnects automatically). Linking never
  re-evaluates mailbox limits; new registrations enforce them.
- `DELETE /internal/v1/devices/{id}`: same as the portal device delete (`RevokeDeviceAsync`).
- `DELETE /internal/v1/users/{id}` (account deletion) unchanged: revokes every device with that owner,
  deletes their subscriptions and credentials. Those devices come back as new ids from the API.
- Portal `GET /v1/me/devices` and `GET /v1/me/mailboxes` unchanged (owner = user).

## R4. Limits per owner key

- `MailboxService.ListUserGroupsAsync(ownerKey)`: for a user owner, all live devices of that user; for a
  device owner, that device alone. Apply `wr_mbx` and `MaxOnlineMailboxesPerUser` to both kinds; remove
  the "skip when unowned" branch.
- Socket caps, connect-rate and register-rate keys switch to `OwnerKey`. Option names stay
  (`MaxConnectionsPerUser` and friends) to avoid config churn; the docs say "per owner".
- `user_mailbox_pauses` stays user-only. Fan-out's pause join already yields no match for unowned
  devices.

## R5. Registration abuse controls

New `RegistrationAbuseGuard` (Redis counters, like `SocketAbuseGuard`), applied in
`MailboxEndpoints.CreateAsync` and `UpdateCredentialsAsync` for `imap` and `dav`:

- `reg:fail:{deviceId}`: incremented when IMAP or DAV validation fails with an auth or unreachable
  reason. At `Api:RegistrationStrikeLimit` (6 per hour) the device gets `reg:ban:{deviceId}` for
  `Api:RegistrationBanMinutes` (60) and new registrations return 429 `device_banned` with `Retry-After`.
- `reg:hosts:{deviceId}`: a Redis set of distinct IMAP hosts in the last 24 hours; above
  `Api:MaxDistinctImapHostsPerDevicePerDay` (8) the request is refused with 422 `host_limit_reached`.
- Existing monitor behavior stays: 3 failures disables the mailbox (`needs_reauth`) until the app
  re-sends credentials.
- `DeviceReaper`: an unowned device whose push token is invalid or unseen for
  `Retention:UnownedDeviceDays` (30) is revoked and purged, which deletes its credentials.

## R6. Platform acceptance

- Confirm `devices.platform` CHECK (migrations 0003 and 0009) and `Socket:AllowedPlatforms` include
  `windows` and `macos`; confirm `POST /v1/devices` accepts a null push token with a socket key for
  desktop. Add an integration test for it. This clears the app's "Windows today" rejection path.

## R7. Contracts and docs

- `RegisterDeviceRequest` gets optional `deviceId`. New problem reasons: `user_token_required`,
  `device_banned`, `host_limit_reached`. Update `README.md` (auth section: two token kinds, internal
  owner endpoint) and `docs/OPERATIONS.md` (new options, Redis keys).

## R8. Tests

- Fix `tests/WinoRelay.IntegrationTests`: mint ES256 tokens locally with a test key and serve a JWKS
  from the fixture (`Auth:Issuer`, `Auth:JwksUrl` pointing at the test server). Replace `X-Api-Key` and
  `deviceSecret`.
- New tests: device-kind token registers with its own id; user-kind token cannot touch a device it
  does not own; owner PUT flips access and disconnects the socket; unowned device hits `wr_mbx` at 10;
  strike and ban; Windows registration with no push token.
- `WinoRelay.Core.Tests`: `RegistrationLimits` for owner keys.

## Phases

1. R1, R2, R3, R4, R6, R8 fixture. Deploy together with API phase 1.
2. R5 abuse guard and reaper rule, R7 docs.

## Operational notes

- Deploy order: relay first (accepts both token kinds), then the API (starts issuing `wr_kind`), then
  the apps. Old app builds keep working because missing `wr_kind` means `user`.
- Migration 0010 renames a column on `device_connections`; the sweeper and `TryInsertAsync` SQL change
  in the same release.
