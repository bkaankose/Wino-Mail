# Platform foundation selective import

Donor: `62858db`. Imported contracts, security/auth adapters, preferences/runtime and their regression tests. Windows adapters use the Windows target; no generated outputs or translations imported.

Rejected: backup-dialog return signature; donor snapshot/CardDAV regression; unrelated WhatsNew changes; CardDAV synchronization donor regression.

## Selected sources

- `src/Wino.Core.Domain/Enums/ApplicationRuntimeState.cs`
- `src/Wino.Core.Domain/Enums/AttachmentFileOperationStatus.cs`
- `src/Wino.Core.Domain/Enums/ModifierKeys.cs`
- `src/Wino.Core.Domain/Enums/PlatformOperationStatus.cs`
- `src/Wino.Core.Domain/Enums/PrintingResult.cs`
- `src/Wino.Core.Domain/Enums/SmimeCertificatePurpose.cs`
- `src/Wino.Core.Domain/Enums/StartupBehaviorResult.cs`
- `src/Wino.Core.Domain/Exceptions/AccountCredentialMissingException.cs`
- `src/Wino.Core.Domain/Interfaces/IAccountCredentialPersistence.cs`
- `src/Wino.Core.Domain/Interfaces/IAccountService.cs`
- `src/Wino.Core.Domain/Interfaces/IApplicationResourceResolver.cs`
- `src/Wino.Core.Domain/Interfaces/IApplicationRuntime.cs`
- `src/Wino.Core.Domain/Interfaces/IAttachmentPlatformService.cs`
- `src/Wino.Core.Domain/Interfaces/IAuthenticator.cs`
- `src/Wino.Core.Domain/Interfaces/IClipboardService.cs`
- `src/Wino.Core.Domain/Interfaces/IExternalLauncher.cs`
- `src/Wino.Core.Domain/Interfaces/IMailPrintPresenter.cs`
- `src/Wino.Core.Domain/Interfaces/IPictureStorageService.cs`
- `src/Wino.Core.Domain/Interfaces/IPlatformCapabilities.cs`
- `src/Wino.Core.Domain/Interfaces/IReaderRuntimeService.cs`
- `src/Wino.Core.Domain/Interfaces/ISecretProtector.cs`
- `src/Wino.Core.Domain/Interfaces/IShellMenuProviderResolver.cs`
- `src/Wino.Core.Domain/Interfaces/IShortcutPlatformService.cs`
- `src/Wino.Core.Domain/Interfaces/ISmimeCertificateService.cs`
- `src/Wino.Core.Domain/Interfaces/IStartupIntegrationService.cs`
- `src/Wino.Core.Domain/Interfaces/ISubstrateTaskTokenProvider.cs`
- `src/Wino.Core.Domain/Interfaces/ITaskCompletionSound.cs`
- `src/Wino.Core.Domain/Models/HotKeyGesture.cs`
- `src/Wino.Core.Domain/Models/KeyboardShortcutContextPolicy.cs`
- `src/Wino.Core.Domain/Models/Attachments/AttachmentPolicyCancellationException.cs`
- `src/Wino.Core.Domain/Models/Calendar/CalendarEventComposeNavigationArgs.cs`
- `src/Wino.Core.Domain/Models/Platform/PlatformCapabilities.cs`
- `src/Wino.Core.Domain/Models/Platform/PlatformOperationResult.cs`
- `src/Wino.Core.Domain/Models/Printing/MailPrintOptions.cs`
- `src/Wino.Core.Domain/Models/Printing/MailPrintRequest.cs`
- `src/Wino.Core.Domain/Entities/Shared/CustomServerInformation.cs`
- `src/Wino.Core.Domain/Entities/Shared/KeyboardShortcut.cs`
- `src/Wino.Core.Domain/Entities/Shared/WinoAccount.cs`
- `src/Wino.Core/CoreContainerSetup.cs`
- `src/Wino.Core/Http/GmailClientMessageHandler.cs`
- `src/Wino.Core/Http/GraphAuthenticationRetryHandler.cs`
- `src/Wino.Core/Http/MicrosoftTokenProvider.cs`
- `src/Wino.Core/Services/ApplicationRuntime.cs`
- `src/Wino.Core/Services/ApplicationRuntimeInitialization.cs`
- `src/Wino.Core/Services/AuthenticationProvider.cs`
- `src/Wino.Core/Services/MigrationAccountAuthorizationService.cs`
- `src/Wino.Core/Services/ProviderFeatureAuthorizationService.cs`
- `src/Wino.Core/Services/WinoRequestProcessor.cs`
- `src/Wino.Services/AccountService.cs`
- `src/Wino.Services/AttachmentFileService.cs`
- `src/Wino.Services/FileSystemApplicationResourceResolver.cs`
- `src/Wino.Services/IntelligenceResultKeyRows.cs`
- `src/Wino.Services/IntelligenceResultKeyStore.cs`
- `src/Wino.Services/KeyboardShortcutService.cs`
- `src/Wino.Services/MimeFileService.cs`
- `src/Wino.Services/PictureStorageService.cs`
- `src/Wino.Services/PreferencesService.cs`
- `src/Wino.Services/ServicesContainerSetup.cs`
- `src/Wino.Services/ThumbnailService.cs`
- `src/Wino.Services/WinoAccountApiClient.cs`
- `src/Wino.Services/WinoAccountProfileService.cs`
- `src/Wino.Services/WinoAccountSessionService.cs`
- `src/Wino.Services/WinoLogger.cs`
- `src/Wino.Services/Dav/DavCredentialStore.cs`
- `src/Wino.Authentication/GmailAuthenticator.cs`
- `src/Wino.Authentication/IGoogleTokenStore.cs`
- `src/Wino.Authentication/IOutlookAuthenticationHost.cs`
- `src/Wino.Authentication/OutlookAuthenticator.cs`
- `src/Wino.Authentication/WinoGmailCodeReceiver.cs`
- `src/Wino.SourceGenerators/Preferences/PreferencesPropertyGenerator.cs`
- `src/Wino.Platform.Windows/DpapiSecretProtector.cs`
- `src/Wino.Platform.Windows/IWindowsAttachmentPolicyService.cs`
- `src/Wino.Platform.Windows/WindowsAccountCredentialPersistence.cs`
- `src/Wino.Platform.Windows/WindowsAttachmentPolicyService.cs`
- `src/Wino.Platform.Windows/WindowsGoogleTokenStore.cs`
- `src/Wino.Platform.Windows/WindowsOutlookAuthenticationHost.cs`
- `src/Wino.Platform.Windows/Services/SmimeCertificateService.cs`
- `tests/Wino.Core.Tests/ApplicationRuntimeTests.cs`
- `tests/Wino.Core.Tests/Authentication/GmailAuthenticatorPresenterTests.cs`
- `tests/Wino.Core.Tests/Authentication/GmailAuthenticatorStateTests.cs`
- `tests/Wino.Core.Tests/Authentication/GmailTokenStoreTests.cs`
- `tests/Wino.Core.Tests/Authentication/OutlookAuthenticationHostTests.cs`
- `tests/Wino.Core.Tests/Authentication/TestGoogleTokenStore.cs`
- `tests/Wino.Core.Tests/Helpers/TestAccountCredentialPersistence.cs`
- `tests/Wino.Core.Tests/Http/AuthenticationRetryHandlerTests.cs`
- `tests/Wino.Core.Tests/Intelligence/IntelligenceResultKeyStoreTests.cs`
- `tests/Wino.Core.Tests/Models/SmimeContextBoundaryTests.cs`
- `tests/Wino.Core.Tests/Services/AccountAliasCapabilityTests.cs`
- `tests/Wino.Core.Tests/Services/AccountCredentialPersistenceTests.cs`
- `tests/Wino.Core.Tests/Services/AccountRecoveryMetadataTests.cs`
- `tests/Wino.Core.Tests/Services/AccountServiceTests.cs`
- `tests/Wino.Core.Tests/Services/AttachmentFileServiceTests.cs`
- `tests/Wino.Core.Tests/Services/AuthenticationTokenMigrationServiceTests.cs`
- `tests/Wino.Core.Tests/Services/DavCredentialStoreTests.cs`
- `tests/Wino.Core.Tests/Services/DraftMimePersistenceTests.cs`
- `tests/Wino.Core.Tests/Services/FolderServiceTests.cs`
- `tests/Wino.Core.Tests/Services/KeyboardShortcutPlatformDefaultsTests.cs`
- `tests/Wino.Core.Tests/Services/MailCopyPersistenceTests.cs`
- `tests/Wino.Core.Tests/Services/MailFetchingTests.cs`
- `tests/Wino.Core.Tests/Services/MailThreadingTests.cs`
- `tests/Wino.Core.Tests/Services/PreferencesPortabilityTests.cs`
- `tests/Wino.Core.Tests/Services/SqliteVariableLimitTests.cs`
- `tests/Wino.Core.Tests/Services/WinoAccountApiClientFailureTests.cs`
- `tests/Wino.Core.Tests/Services/WinoAccountApiClientSessionTests.cs`
- `tests/Wino.Core.Tests/Services/WinoAccountIntelligenceSnapshotServiceTests.cs`
- `tests/Wino.Core.Tests/Services/WinoAccountProfileServiceTests.cs`
- `tests/Wino.Core.Tests/Services/WinoAccountSessionServiceTests.cs`
- `tests/Wino.Core.Tests/Services/WinoPurchaseReconciliationServiceTests.cs`
- `tests/Wino.Core.Tests/Telemetry/WinoTelemetryPolicyTests.cs`

- `src/Wino.Core.Domain/Models/MailItem/HtmlPreviewVisitor.cs` — host-supplied S/MIME context.
- `tests/Wino.Core.Tests/Models/HtmlPreviewVisitorTests.cs`

## Compatibility and lifecycle

- Windows uses CurrentUser DPAPI with unchanged DAV/account result-key entropy and existing Google/MSAL cache locations. Its account credential persistence adapter preserves existing SQLite fields without migration.
- Shared preferences retain all original public preference members and storage keys; the generator emits accessors only in the owning Services assembly.
- Runtime initialization is checkpointed and process-owned. Hosts own dispatch, presentation and shutdown integration; shared services no longer choose Windows defaults.
- macOS account credentials use immutable, account/purpose-bound Keychain revisions. All persisted incoming/outgoing/CalDAV passwords and Wino Account access/refresh tokens are covered. CardDAV password persistence uses the encrypted DAV store; its endpoint URL remains nonsecret metadata.
- Old Keychain revisions are retained during updates so a failed SQLite write cannot delete the last usable credential. Account deletion removes the account scope. Reconciliation of abandoned revisions after failed writes and backup/import recovery remains a lifecycle limitation requiring a later committed-reference audit.
- Manual Windows login/refresh/storage/runtime checks and native Keychain denial/loss/restart checks remain pending. Compile/unit-test evidence does not demonstrate native behavior.

## Executed verification

- Portable Services/Auth/Core and consolidated portable ViewModel target compiled through Core.Tests.
- Foundation filter: 28 passed, 0 failed (`artifacts/platform-foundation-tests.log`).
- Adjacent recovery/auth/S-MIME/shortcuts/attachments/account/snapshot/CardDAV filter: 109 passed; one Google callback response socket reset (`artifacts/platform-regression-tests.log`). Fixed final browser response completion before listener disposal; affected auth/session filter then passed 14/14 (`artifacts/platform-auth-final-tests.log`).
- `Wino.Platform.Windows` and local-lab `DatabaseGenerator` Debug x64 build: 0 errors/warnings (`artifacts/platform-lab-build.log`).
- Intelligence console Debug x64 build: 0 errors/warnings (`artifacts/platform-console-build.log`). Console now references the Windows platform target and supplies native DPAPI/auth registrations without loading WinUI.
- New English source translation keys only: credential recovery, generic operation failure and consent version/change strings required by imported shared VMs. Other locales unchanged.


## Integration review corrections

- Frozen legacy Core.ViewModels owns the previous `INativeAppService` and `WindowsTaskbarPosition` source under `Legacy` without changing deprecated UWP application sources. The production Domain/Core/Services/Auth/Mail.ViewModels graph contains neither native handle contract.
- Billing launches use `IExternalLauncher`; Microsoft Store fallback/redemption consults `IPlatformCapabilities`. Windows capability registrations keep existing Store behavior, while macOS uses Wino Account entitlement. Store-unavailable tests imported with the selected capability changes.
- Runtime shutdown awaits actual job/provider cancellation completion. Caller cancellation cancels that caller's wait; it cannot report the runtime stopped prematurely. Non-cancellation teardown failures are logged and surfaced with Faulted state.
- Review regression filter (runtime, Store capability, purchase reconciliation): 26/26 passed (`artifacts/platform-integration-review-tests.log`). Both affected Windows tools rebuilt without warnings/errors after the legacy contract move.
- Release scripts parsed and dot-sourced successfully; release target XML and profile JSON validated. Pure release-plan/profile/argument checks resolve the active WinUI project and preserve Release/x64/win-x64. No release-specific test files were discovered in the tracked repository. No packaging or publication was invoked by these script checks.

## Notification capability integration

Synchronization initialization now consumes the host notification capability. Successful mail synchronization skips native notification/badge publication when the feature is unavailable; calendar badge and credential-attention notification calls use the same gate. The original public initialization signature remains as a compatibility overload with notifications enabled; active host initialization passes real capabilities explicitly. Windows capabilities preserve its existing behavior.
