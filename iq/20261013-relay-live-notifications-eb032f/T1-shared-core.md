# T1 shared core: report

## Checkpoints

- 13 Oct 2026: read plan, relay contracts (Contracts.cs, Filters.cs, MailboxService, PushPayload), API relay doc and the shared code the plan names.
- Domain types, Services/PushRelay, runtime, startup, sign-out hook and DI written; Wino.Core build passes.
- Translations and ViewModel members done; Wino.Mail.ViewModels build passes.
- Focused tests pass: Core filter 105/105, ViewModels NotificationSettings 16/16.
- Full test projects run (one unrelated failure, see Verification). Done. Nothing committed.

## Files

Added:
- `src/Wino.Core.Domain/Models/PushRelay/`: `PushRelayDefaults.cs` (defaults plus `PushRelayPlatforms`, `PushRelayProviders`, `PushRelayImapSecurity`, `PushRelayMailboxStatuses`, `PushRelayPushKind`, `PushRelayPayloadKeys`), `RelayTokenResult.cs` (`RelayTokenDto`, `RelayApiErrorCodes`, `PushRelayAccessToken`), `PushRelayContracts.cs` (all relay DTOs, `PushRelayMailboxRegistrationRequest`), `PushRelayProblem.cs` (`PushRelayProblem`, `PushRelayErrorCodes`, `PushRelayAuthReasons`), `PushRelayDeviceIdentity.cs` (`PushRelayDeviceIdentity`, `PushRelayDeviceDescriptor`), `PushRelayState.cs` (`PushRelayState`, `PushRelayMailboxRegistration`), `PushRelayRegistrationSnapshot.cs` (`PushRelayRegistrationSnapshot`, `PushRelayMailboxSnapshot`).
- `src/Wino.Core.Domain/Enums/PushRelayEnums.cs`: `PushRelaySyncReason`, `PushRelayAvailability`, `PushRelayNotificationOutcome`, `PushRelayPushTestResult`.
- `src/Wino.Core.Domain/Exceptions/PushRelayApiException.cs`.
- `src/Wino.Core.Domain/Interfaces/`: `IPushRelayTokenProvider`, `IPushRelayClient`, `IPushRelayDeviceIdentityStore`, `IPushRelayDeviceInfo`, `IPushTokenProvider`, `IPushRelayStateStore`, `IPushRelayMailboxRegistrationBuilder`, `IPushRelayRegistrationService`, `IPushRelayNotificationHandler`.
- `src/Wino.Services/PushRelay/`: `PushRelayJsonContext` (public, so platform identity stores can serialize `PushRelayDeviceIdentity`), `PushRelayTokenProvider`, `PushRelayClient`, `ConfigurationPushRelayStateStore`, `ImapPushRelayMailboxRegistrationBuilder`, `PushRelayRegistrationService`, `PushRelayNotificationHandler`.
- Tests: `tests/Wino.Core.Tests/Services/PushRelay/` (`PushRelayTestDoubles`, `WinoAccountApiClientRelayTokenTests`, `PushRelayTokenProviderTests`, `PushRelayClientTests`, `ImapPushRelayMailboxRegistrationBuilderTests`, `PushRelayRegistrationServiceTests`, `PushRelayNotificationHandlerTests`), `tests/Wino.Core.Tests/Services/WinoAccountProfileServicePushRelayTests.cs`, `tests/Wino.Mail.ViewModels.Tests/NotificationSettingsPushRelayTests.cs`.

Changed:
- `IPlatformCapabilities` / `PlatformCapabilities`: `RemoteNotifications`, `PushRelayOptIn` (trailing optional record parameters, default false; existing constructions compile unchanged).
- `IApplicationRuntime` / `ApplicationRuntime`: `SynchronizeAccountInboxAsync`.
- `IWinoAccountApiClient` / `WinoAccountApiClient`: `CreateRelayTokenAsync` and `ApiEnvelope<RelayTokenDto>` in `WinoAccountApiJsonContext`. No other implementers or hand-written fakes exist (tests use Moq).
- `WinoAccountProfileService`: optional `Func<IPushRelayRegistrationService>? pushRelay` constructor parameter; `SignOutAsync` calls relay `SignOutAsync` before `_sessions.ReplaceAsync`, inside try/catch.
- `ApplicationRuntimeInitialization`: optional `Func<IPushRelayRegistrationService>? pushRelay`; `RunBackgroundStartupAsync` also starts the relay service (errors logged, never thrown). `CoreContainerSetup` passes it.
- `ServicesContainerSetup`: `RegisterPushRelayServices`.
- `NotificationSettingsPageViewModel` (shared copy only) and `en_US/resources.json`.
- `tests/Wino.Core.Tests/ApplicationRuntimeTests.cs`: two `SynchronizeAccountInbox_*` tests.

## What the heads must implement and register

All in `Wino.Core.Domain.Interfaces`; models in `Wino.Core.Domain.Models.PushRelay`.

```csharp
public interface IPushRelayDeviceIdentityStore
{
    Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default);   // null when missing or corrupt
    Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
public interface IPushRelayDeviceInfo { PushRelayDeviceDescriptor Describe(); }
public interface IPushTokenProvider
{
    bool IsPushChannelAvailable { get; }
    string? CurrentToken { get; }          // lowercase hex
    string? LastFailure { get; }
    event EventHandler? TokenChanged;      // only on change
}
public sealed record PushRelayDeviceIdentity(Guid DeviceId, Guid WinoUserId, string Platform, DateTime RegisteredAtUtc);
public sealed record PushRelayDeviceDescriptor(string Platform, string? Name, string? OsVersion);   // PushRelayPlatforms.Ios/MacOS/Windows
```

- Serialize the identity with `Wino.Services.PushRelay.PushRelayJsonContext.Default.PushRelayDeviceIdentity`.
- Capabilities: iOS `RemoteNotifications: true, PushRelayOptIn: false`; Mac `true, true`; Windows `false, true`.
- Heads call (shared, already registered): `IPushRelayRegistrationService.RequestSync(PushRelaySyncReason.Foreground)`, `.CheckInAsync(ct)` (iOS background refresh), and `IPushRelayNotificationHandler.HandleAsync(IReadOnlyDictionary<string,string> payload, ct)` returning `PushRelayNotificationOutcome` (`Ignored`, `NoData`, `NewData`, `Failed`).
- No head file needed an edit for this task: the new constructor parameters are optional or resolved by DI, and the new capability members default to false.

## DI and fallback behaviour

- Shared singletons: `IPushRelayTokenProvider`, `IPushRelayClient`, `IPushRelayStateStore` (configuration keys `PushRelay.OptIn`, `PushRelay.State`, non-roaming), `IPushRelayMailboxRegistrationBuilder` (IMAP), `IPushRelayRegistrationService`, `Func<IPushRelayRegistrationService>`, `IPushRelayNotificationHandler` (resolves `IApplicationRuntime` lazily).
- `PushRelayRegistrationService` is built with a factory that uses `GetService` (not `GetRequiredService`) for `IPushRelayDeviceIdentityStore`, `IPushRelayDeviceInfo` and `IPushTokenProvider`. No fallback implementations are registered. When the identity store or device info is missing, or both capability flags are false, availability is `UnsupportedPlatform`, no subscriptions or timer start, and nothing is sent. A missing `IPushTokenProvider` means "no push token". Every head resolves today without registering anything new. Required services from the heads: `IPlatformCapabilities`, `IAppMetadataService`, `IConfigurationService` (all three heads already register them).

## ViewModel members (`Wino.Core.ViewModels.NotificationSettingsPageViewModel`)

Constructor gains `IPushRelayRegistrationService pushRelay, IPlatformCapabilities capabilities` (resolved by DI).

| Member | Type | Meaning |
| --- | --- | --- |
| `IsPushRelaySectionVisible` | bool | `RemoteNotifications or PushRelayOptIn` |
| `IsPushRelayToggleVisible` | bool | opt-in platforms (Mac, Windows) |
| `IsPushRelayEnabled` | bool, two-way | switch; setter calls `SetUserOptInAsync`. On iOS it mirrors `Enabled`. |
| `IsPushRelayToggleEnabled` | bool | false when unsupported or signed out (still true to switch off when signed out but opted in) |
| `IsPushRelaySignInRequired` | bool | show the sign-in InfoBar |
| `PushRelayStatusText` | string | status line |
| `PushRelayMailboxWarningText` | string | "could not sign in to {accounts}", empty when none |
| `IsPushTestVisible` | bool | `RemoteNotifications`, enabled and registered |
| `SendPushTestCommand` | IAsyncRelayCommand | sends a test push |
| `PushRelayTestResultText` | string | outcome of the last test |
| `PushRelayAvailability` | enum | raw availability |
| `ApplyPushRelaySnapshot(snapshot)` | method | public, used internally |

The VM subscribes to `SnapshotChanged` in `OnNavigatedTo` and unsubscribes in `OnNavigatedFrom`. `ResetAsync` does not touch the opt-in.

## Translation keys (en_US only)

`NotificationSettings_PushRelay_Title`, `_Description`, `_SignInRequired`, `_Off`, `_RegisteredFormat`, `_ErrorFormat`, `_ServiceUnavailable`, `_PlatformUnsupported`, `_LimitReached`, `_SendTest`, `_TestQueued`, `_TestNoToken`, `_Mailbox_NeedsReauth`.

## Verification

- `dotnet build src/Wino.Services/Wino.Services.csproj -c Debug`: succeeded.
- `dotnet build src/Wino.Core/Wino.Core.csproj -c Debug`: succeeded. A `--no-incremental` build showed no warnings in any new or changed file.
- `dotnet build src/Wino.Mail.ViewModels/Wino.Mail.ViewModels.csproj -c Debug`: succeeded.
- `dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj --filter "FullyQualifiedName~PushRelay|FullyQualifiedName~WinoAccountApiClient|FullyQualifiedName~WinoAccountProfileService|FullyQualifiedName~ApplicationRuntime"`: 105 passed, 0 failed.
- `dotnet test tests/Wino.Mail.ViewModels.Tests/Wino.Mail.ViewModels.Tests.csproj --filter "FullyQualifiedName~NotificationSettings"`: 16 passed.
- Full `Wino.Core.Tests`: 1186 passed, 1 failed: `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce` ("ReceivedCount to be 1, but found 2"). It fails the same way when run alone. It covers recipient history, which this change does not touch, and none of its files are modified. It was not checked against a clean checkout, because the instructions forbid stash or checkout.
- Full `Wino.Mail.ViewModels.Tests`: 202 passed.
- Not run (other owners): the WinUI Debug build and Release gate, the Mac and iOS builds, and manual checks.

## Deviations from the plan

- `IApplicationRuntime.SynchronizeAccountInboxAsync` returns `Task<bool>` (true when the inbox sync succeeded or one was already running), so the handler can map `NewData` or `Failed`.
- `IPushRelayTokenProvider` also exposes `int? MailboxLimit`, read from the token's `wr_mbx` claim for display only. `GetTokenAsync` returns `PushRelayAccessToken`, whose `ToString` hides the token.
- The relay sends status in the `st` payload key. The handler reads it as well as `mr` and `mm`.
- `IPushRelayRegistrationService.SendPushTestAsync` returns `PushRelayPushTestResult` (`Queued`, `NoPushToken` (409), `NotRegistered`, `Failed`).
- `ReportMailboxStatus(..., needs_reauth, ...)` clears the fingerprint itself and queues a `CredentialsChanged` pass. The handler still calls `RequestSync` as planned; the requests are coalesced.
- A 400 `invalid_request` on register marks the platform rejected for this app version on any platform, not only Windows. Name and OS version are trimmed to 64 characters first, so the request is otherwise valid.
- Check-in (`GET /v1/devices/me`) also runs for the `UserRequested` and `DeviceRevoked` reasons, so a revoked device is found right away.
- `PUT /v1/devices/me/accounts` runs only after register, patch or check-in, or when the eligible account set changes. This keeps the plan's "no-op within 20 h".
- A mailbox that failed with the same credentials (for example `provider_auth_failed` or the limit) is retried only on a fresh device view or a `UserRequested` pass, not on every pass. `provider_unreachable` (502) is handled per mailbox and is not transient.
- Sign-out waits up to 5 s for a running pass (which it cancels), then spends up to 5 s on `DELETE`, so the worst case is about 10 s.

## Left for others

- Platform adapters, registrations, capability values, settings UI and push forwarding for iOS, Mac and Windows (plan section 4).
- Head builds and manual checks (plan section 6).
