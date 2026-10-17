# Push relay: Wino Mail API plan (device identity)

Companion to [push-relay-design.md](../push-relay-design.md). Other plans: [app](plan-app.md),
[relay](plan-relay.md). Repository: Wino-Mail-API (`main`). File references verified on 2026-10-17.

## What exists today (verified)

- `POST /api/v1/relay/token` (`Controllers/RelayController.cs`) is `[Authorize]` only. `RelayTokenService`
  mints ES256 JWTs with `sub = userId`, `aud = winorelay`, `wr_mbx = Relay:MaxMailboxesPerUser` (10),
  30 minute lifetime. JWKS at `/.well-known/jwks.json`.
- Two rate-limit policies (`auth-anon` 20/min per IP, `auth-user` 120/min) are defined in `Program.cs`
  but attached to no endpoint.
- No device, attestation, nonce or challenge code. `AppStoreSignedDataVerifier` already pins Apple Root
  CA G3 and builds a custom-root `X509Chain`; reusable for the App Attest chain.
- Account deletion and moderation write `RelayUserEvent` outbox rows (`user.deleted`, `user.revoked`);
  `RelayEventWorker` delivers them to `/internal/v1/users/{id}` with a service token.
- EF Core on PostgreSQL, migrations under `Data/Migrations`, applied by `AdminSeeder` at startup.
  Tests use `TestApiFactory` with a per-test database; no relay tests exist.
- Secrets live under `Secrets:*` (Key Vault names use `--`), options under typed classes.

## Goal

A device can obtain relay tokens without a Wino Account, after proving it is the genuine Wino app where
the platform supports that. A Wino Account sign-in links the device; sign-out unlinks it; account
deletion drops everything the account owned.

## A1. Data model

New entity `RelayDevice` (table `RelayDevices`):

| Column | Notes |
| --- | --- |
| `Id` (uuid, PK) | `Guid.CreateVersion7()`; this id is also the relay's `devices.id` |
| `Platform` | `ios`, `android`, `windows`, `macos` |
| `AttestationKind` | `AppAttest`, `PlayIntegrity`, `None` |
| `AttestationKeyId` | App Attest key id, or Play Integrity package + certificate digest |
| `AttestationPublicKey` | App Attest public key (DER) for assertions; null otherwise |
| `AssertionCounter` | App Attest monotonic counter |
| `CredentialHash` | SHA-256 of the device credential (32 random bytes, base64url) |
| `UserId` (uuid, null, FK Users, `ON DELETE SET NULL`) | Owner when linked |
| `CreatedAt`, `LastSeenAt`, `RevokedAt` | UTC |
| `CreatedByIp`, `UserAgent` | Same as `RefreshTokenSession` |

New entity `RelayDeviceChallenge` (table `RelayDeviceChallenges`): `Nonce` (PK, 32 bytes base64url),
`Platform`, `CreatedAt`, `ExpiresAt` (2 minutes), `CreatedByIp`. Rows are deleted on use and swept by
`RelayEventWorker` every pass.

Extend `RelayUserEvent.Type` with `device.linked`, `device.unlinked`, `device.revoked` and an optional
`DeviceId` column.

Migration: `AddRelayDevices`.

## A2. Endpoints

All under `Controllers/RelayController.cs` (route `api/v1/relay`), envelope responses as everywhere.

| Endpoint | Auth | Purpose |
| --- | --- | --- |
| `POST devices/challenge` `{platform}` | anonymous, `auth-anon` | Returns `{nonce, expiresUtc}` |
| `POST devices` `{platform, nonce, attestation}` | anonymous, `auth-anon` | Verifies the attestation, creates `RelayDevice`, returns `{deviceId, credential, expiresUtc}` |
| `POST token` | user bearer **or** device credential header | Unchanged for users. With `X-Relay-Device: <id>` and `X-Relay-Device-Credential: <secret>` and no bearer, mints a device token |
| `POST devices/{id}/link` | user bearer + device credential headers | Sets `UserId`, notifies the relay, returns the device |
| `POST devices/{id}/unlink` | device credential headers | Clears `UserId`, notifies the relay |
| `GET devices` | user bearer | The user's linked devices (for the Wino Account page) |
| `DELETE devices/{id}` | user bearer or device credential | Revokes the device and notifies the relay |

`attestation` is a discriminated object: `{kind: "appattest", keyId, attestationObject}`,
`{kind: "playintegrity", token}` or `{kind: "none"}`. `none` is accepted only for `windows` and `macos`;
a mobile platform with `none` returns 422 `ATTESTATION_REQUIRED`. Those devices use the user token path.

Device-token claims: `sub = deviceId`, `wr_kind = device`, `wr_mbx = Relay:MaxMailboxesPerDevice`
(10), `aud = winorelay`, 30 minutes. User tokens gain `wr_kind = user`. The relay plan (R1) reads
`wr_kind`.

Assertions: a device created with App Attest must send `X-Relay-Device-Assertion` on `POST token`,
`link`, `unlink` and `DELETE`: an assertion over SHA-256 of `"{deviceId}|{nonce}"` where the nonce comes
from a fresh `devices/challenge`. The API verifies the signature with the stored public key and that
the counter increased. Play Integrity devices and `none` devices rely on the credential alone.

## A3. Verification services

- `IAppAttestVerifier` (`Infrastructure/Relay/Attestation/AppAttestVerifier.cs`): parse the CBOR
  attestation object, verify the certificate chain against the Apple App Attest Root CA (pinned by
  SHA-256 like `AppStoreTrustAnchors`), check the nonce extension (OID 1.2.840.113635.100.8.2) equals
  SHA-256(authData ‖ SHA-256(clientData)), check the RP ID hash equals SHA-256 of
  `"{TeamId}.{BundleId}"`, counter is 0, `aaguid` is `appattest` or `appattestdevelop` depending on
  `Secrets:Attestation:Apple:Environment`, and the key id equals SHA-256 of the credential public key.
  A CBOR reader is needed: `System.Formats.Cbor` is in the BCL; no new package.
- `IPlayIntegrityVerifier`: calls
  `https://playintegrity.googleapis.com/v1/{package}:decodeIntegrityToken` with a service-account
  token (`Google.Apis.Auth` is already referenced; use `GoogleCredential` with the Play Integrity scope).
  Accepts when `requestDetails.nonce` matches, `requestPackageName` is `com.winomail.android`,
  `appIntegrity.appRecognitionVerdict == PLAY_RECOGNIZED`, `deviceIntegrity` contains
  `MEETS_DEVICE_INTEGRITY`, and `requestDetails.timestampMillis` is within 2 minutes. A
  `Secrets:Attestation:Google:AllowUnrecognizedApp` flag (Development only) lets debug builds pass.
- Nonce handling: `RelayDeviceChallengeService` creates and consumes rows under the `23505` rule
  (delete-on-use is the consumption guard).

## A4. Relay synchronization

- Synchronous call with outbox fallback: `IRelayAdminClient` wraps the existing named `HttpClient`
  "Relay" and service token. `link` calls `PUT /internal/v1/devices/{id}/owner {userId}`; `unlink` sends
  `{userId: null}`; `DELETE` calls `DELETE /internal/v1/devices/{id}`. On failure the matching
  `RelayUserEvent` row is written and `RelayEventWorker` retries with its existing backoff.
- `AccountDeletionService`: before the transaction, load the user's `RelayDevice` rows; inside it,
  delete them (the FK is `SET NULL` for safety, but deletion is explicit). The existing `user.deleted`
  event already makes the relay drop every device and mailbox owned by the user; devices re-register
  anonymously on next launch (new attestation, new credential).
- `UserModerationService` is unchanged: `user.revoked` already drops owned devices on the relay.

## A5. Limits and rate limits

- Attach `auth-anon` to `devices/challenge` and `devices`, and a new `relay-device` policy (30/min per
  device id, fallback IP) to the device-credential paths of `token`, `link`, `unlink`.
- Per IP: at most 20 device creations per hour (`Relay:MaxDeviceCreationsPerIpPerHour`), enforced in
  the service with a Redis-free check against `RelayDevices.CreatedByIp` in the last hour.
- `Relay:MaxMailboxesPerDevice` (10) is the `wr_mbx` of device tokens; `MaxMailboxesPerUser` stays 10.

## A6. Configuration

```
Relay:MaxMailboxesPerDevice = 10
Relay:MaxDeviceCreationsPerIpPerHour = 20
Secrets:Attestation:Apple:TeamId = 4VB7YWRAQ9
Secrets:Attestation:Apple:BundleIds:0 = com.winomail.macos
Secrets:Attestation:Apple:Environment = production | development
Secrets:Attestation:Google:PackageName = com.winomail.android
Secrets:Attestation:Google:ServiceAccountJson = (Key Vault)
Secrets:Attestation:Google:AllowUnrecognizedApp = false
```

Key Vault names follow the existing `Secrets--Attestation--...` pattern.

## A7. Contracts

New DTOs stay API-local (`Infrastructure/Relay/RelayModels.cs`) like `RelayTokenDto`, so no
`Wino.Mail.Contracts` release is needed. Error codes added to `RelayErrorCodes`: `ATTESTATION_REQUIRED`,
`ATTESTATION_INVALID`, `CHALLENGE_EXPIRED`, `DEVICE_REVOKED`, `DEVICE_NOT_LINKED`,
`DEVICE_OWNED_BY_OTHER_USER` (link moves the device anyway and returns 200; this code is reserved for
a future policy).

## A8. Tests

- `RelayDeviceTests`: challenge → create with `none` on `windows` → token has `wr_kind = device`;
  `none` on `ios` → 422; expired nonce → 400; link and unlink flip `UserId` and enqueue or send the relay
  call (fake `IRelayAdminClient`); account deletion removes devices and writes `user.deleted`.
- `AppAttestVerifierTests` with a locally generated test chain (reuse `AppStoreTestSigner` approach)
  and a Play Integrity fake HTTP handler.
- Set `Relay:*` and a test ECDSA key in `TestApiFactory` so `POST token` is testable.

## Phases

1. A1, A2 (`none` kind only), A4, A5, A6, A8 basics. Deploy. The app's phase 1 depends on this.
2. A3 App Attest and Play Integrity verifiers plus assertion checks.
3. `GET devices` for the Wino Account page.

## Operations checklist

- Create the Play Integrity service account in the Google Cloud project linked from Play Console and
  store its JSON in Key Vault.
- No Apple server credentials are needed; the App Attest root certificate is public.
- Document the device endpoints in `docs/relay.md`.
