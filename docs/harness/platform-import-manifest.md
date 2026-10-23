# Platform foundation selective import

Donor: `62858db`. Imported contracts, security/auth adapters, preferences/runtime and their regression tests. Windows adapters use the Windows target; no generated outputs or translations imported.

Rejected: backup-dialog return signature; donor snapshot/CardDAV regression; unrelated WhatsNew changes; CardDAV synchronization donor regression.

## Selected sources

- `src/MailCal.Core.Domain/Enums/ApplicationRuntimeState.cs`
- `src/MailCal.Core.Domain/Enums/AttachmentFileOperationStatus.cs`
- `src/MailCal.Core.Domain/Enums/ModifierKeys.cs`
- `src/MailCal.Core.Domain/Enums/PlatformOperationStatus.cs`
- `src/MailCal.Core.Domain/Enums/PrintingResult.cs`
- `src/MailCal.Core.Domain/Enums/SmimeCertificatePurpose.cs`
- `src/MailCal.Core.Domain/Enums/StartupBehaviorResult.cs`
- `src/MailCal.Core.Domain/Exceptions/AccountCredentialMissingException.cs`
- `src/MailCal.Core.Domain/Interfaces/IAccountCredentialPersistence.cs`
- `src/MailCal.Core.Domain/Interfaces/IAccountService.cs`
- `src/MailCal.Core.Domain/Interfaces/IApplicationResourceResolver.cs`
- `src/MailCal.Core.Domain/Interfaces/IApplicationRuntime.cs`
- `src/MailCal.Core.Domain/Interfaces/IAttachmentPlatformService.cs`
- `src/MailCal.Core.Domain/Interfaces/IAuthenticator.cs`
- `src/MailCal.Core.Domain/Interfaces/IClipboardService.cs`
- `src/MailCal.Core.Domain/Interfaces/IExternalLauncher.cs`
- `src/MailCal.Core.Domain/Interfaces/IMailPrintPresenter.cs`
- `src/MailCal.Core.Domain/Interfaces/IPictureStorageService.cs`
- `src/MailCal.Core.Domain/Interfaces/IPlatformCapabilities.cs`
- `src/MailCal.Core.Domain/Interfaces/IReaderRuntimeService.cs`
- `src/MailCal.Core.Domain/Interfaces/ISecretProtector.cs`
- `src/MailCal.Core.Domain/Interfaces/IShellMenuProviderResolver.cs`
- `src/MailCal.Core.Domain/Interfaces/IShortcutPlatformService.cs`
- `src/MailCal.Core.Domain/Interfaces/ISmimeCertificateService.cs`
- `src/MailCal.Core.Domain/Interfaces/IStartupIntegrationService.cs`
- `src/MailCal.Core.Domain/Interfaces/ISubstrateTaskTokenProvider.cs`
- `src/MailCal.Core.Domain/Interfaces/ITaskCompletionSound.cs`
- `src/MailCal.Core.Domain/Models/HotKeyGesture.cs`
- `src/MailCal.Core.Domain/Models/KeyboardShortcutContextPolicy.cs`
- `src/MailCal.Core.Domain/Models/Attachments/AttachmentPolicyCancellationException.cs`
- `src/MailCal.Core.Domain/Models/Calendar/CalendarEventComposeNavigationArgs.cs`
- `src/MailCal.Core.Domain/Models/Platform/PlatformCapabilities.cs`
- `src/MailCal.Core.Domain/Models/Platform/PlatformOperationResult.cs`
- `src/MailCal.Core.Domain/Models/Printing/MailPrintOptions.cs`
- `src/MailCal.Core.Domain/Models/Printing/MailPrintRequest.cs`
- `src/MailCal.Core.Domain/Entities/Shared/CustomServerInformation.cs`
- `src/MailCal.Core.Domain/Entities/Shared/KeyboardShortcut.cs`
- `src/MailCal.Core.Domain/Entities/Shared/MailCalAccount.cs`
- `src/MailCal.Core/CoreContainerSetup.cs`
- `src/MailCal.Core/Http/GmailClientMessageHandler.cs`
- `src/MailCal.Core/Http/GraphAuthenticationRetryHandler.cs`
- `src/MailCal.Core/Http/MicrosoftTokenProvider.cs`
- `src/MailCal.Core/Services/ApplicationRuntime.cs`
- `src/MailCal.Core/Services/ApplicationRuntimeInitialization.cs`
- `src/MailCal.Core/Services/AuthenticationProvider.cs`
- `src/MailCal.Core/Services/MigrationAccountAuthorizationService.cs`
- `src/MailCal.Core/Services/ProviderFeatureAuthorizationService.cs`
- `src/MailCal.Core/Services/MailCalRequestProcessor.cs`
- `src/MailCal.Services/AccountService.cs`
- `src/MailCal.Services/AttachmentFileService.cs`
- `src/MailCal.Services/FileSystemApplicationResourceResolver.cs`
- `src/MailCal.Services/IntelligenceResultKeyRows.cs`
- `src/MailCal.Services/IntelligenceResultKeyStore.cs`
- `src/MailCal.Services/KeyboardShortcutService.cs`
- `src/MailCal.Services/MimeFileService.cs`
- `src/MailCal.Services/PictureStorageService.cs`
- `src/MailCal.Services/PreferencesService.cs`
- `src/MailCal.Services/ServicesContainerSetup.cs`
- `src/MailCal.Services/ThumbnailService.cs`
- `src/MailCal.Services/MailCalAccountApiClient.cs`
- `src/MailCal.Services/MailCalAccountProfileService.cs`
- `src/MailCal.Services/MailCalAccountSessionService.cs`
- `src/MailCal.Services/MailCalLogger.cs`
- `src/MailCal.Services/Dav/DavCredentialStore.cs`
- `src/MailCal.Authentication/GmailAuthenticator.cs`
- `src/MailCal.Authentication/IGoogleTokenStore.cs`
- `src/MailCal.Authentication/IOutlookAuthenticationHost.cs`
- `src/MailCal.Authentication/OutlookAuthenticator.cs`
- `src/MailCal.Authentication/MailCalGmailCodeReceiver.cs`
- `src/MailCal.SourceGenerators/Preferences/PreferencesPropertyGenerator.cs`
- `src/MailCal.Platform.Windows/DpapiSecretProtector.cs`
- `src/MailCal.Platform.Windows/IWindowsAttachmentPolicyService.cs`
- `src/MailCal.Platform.Windows/WindowsAccountCredentialPersistence.cs`
- `src/MailCal.Platform.Windows/WindowsAttachmentPolicyService.cs`
- `src/MailCal.Platform.Windows/WindowsGoogleTokenStore.cs`
- `src/MailCal.Platform.Windows/WindowsOutlookAuthenticationHost.cs`
- `src/MailCal.Platform.Windows/Services/SmimeCertificateService.cs`
- `tests/MailCal.Core.Tests/ApplicationRuntimeTests.cs`
- `tests/MailCal.Core.Tests/Authentication/GmailAuthenticatorPresenterTests.cs`
- `tests/MailCal.Core.Tests/Authentication/GmailAuthenticatorStateTests.cs`
- `tests/MailCal.Core.Tests/Authentication/GmailTokenStoreTests.cs`
- `tests/MailCal.Core.Tests/Authentication/OutlookAuthenticationHostTests.cs`
- `tests/MailCal.Core.Tests/Authentication/TestGoogleTokenStore.cs`
- `tests/MailCal.Core.Tests/Helpers/TestAccountCredentialPersistence.cs`
- `tests/MailCal.Core.Tests/Http/AuthenticationRetryHandlerTests.cs`
- `tests/MailCal.Core.Tests/Intelligence/IntelligenceResultKeyStoreTests.cs`
- `tests/MailCal.Core.Tests/Models/SmimeContextBoundaryTests.cs`
- `tests/MailCal.Core.Tests/Services/AccountAliasCapabilityTests.cs`
- `tests/MailCal.Core.Tests/Services/AccountCredentialPersistenceTests.cs`
- `tests/MailCal.Core.Tests/Services/AccountRecoveryMetadataTests.cs`
- `tests/MailCal.Core.Tests/Services/AccountServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/AttachmentFileServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/AuthenticationTokenMigrationServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/DavCredentialStoreTests.cs`
- `tests/MailCal.Core.Tests/Services/DraftMimePersistenceTests.cs`
- `tests/MailCal.Core.Tests/Services/FolderServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/KeyboardShortcutPlatformDefaultsTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCopyPersistenceTests.cs`
- `tests/MailCal.Core.Tests/Services/MailFetchingTests.cs`
- `tests/MailCal.Core.Tests/Services/MailThreadingTests.cs`
- `tests/MailCal.Core.Tests/Services/PreferencesPortabilityTests.cs`
- `tests/MailCal.Core.Tests/Services/SqliteVariableLimitTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalAccountApiClientFailureTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalAccountApiClientSessionTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalAccountIntelligenceSnapshotServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalAccountProfileServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalAccountSessionServiceTests.cs`
- `tests/MailCal.Core.Tests/Services/MailCalPurchaseReconciliationServiceTests.cs`
- `tests/MailCal.Core.Tests/Telemetry/MailCalTelemetryPolicyTests.cs`

- `src/MailCal.Core.Domain/Models/MailItem/HtmlPreviewVisitor.cs` — host-supplied S/MIME context.
- `tests/MailCal.Core.Tests/Models/HtmlPreviewVisitorTests.cs`

## Compatibility and lifecycle

- Windows uses CurrentUser DPAPI with unchanged DAV/account result-key entropy and existing Google/MSAL cache locations. Its account credential persistence adapter preserves existing SQLite fields without migration.
- Shared preferences retain all original public preference members and storage keys; the generator emits accessors only in the owning Services assembly.
- Runtime initialization is checkpointed and process-owned. Hosts own dispatch, presentation and shutdown integration; shared services no longer choose Windows defaults.
- macOS account credentials use immutable, account/purpose-bound Keychain revisions. All persisted incoming/outgoing/CalDAV passwords and online account access/refresh tokens are covered. CardDAV password persistence uses the encrypted DAV store; its endpoint URL remains nonsecret metadata.
- Old Keychain revisions are retained during updates so a failed SQLite write cannot delete the last usable credential. Account deletion removes the account scope. Reconciliation of abandoned revisions after failed writes and backup/import recovery remains a lifecycle limitation requiring a later committed-reference audit.
- Manual Windows login/refresh/storage/runtime checks and native Keychain denial/loss/restart checks remain pending. Compile/unit-test evidence does not demonstrate native behavior.

## Executed verification

- Portable Services/Auth/Core and consolidated portable ViewModel target compiled through Core.Tests.
- Foundation filter: 28 passed, 0 failed (`artifacts/platform-foundation-tests.log`).
- Adjacent recovery/auth/S-MIME/shortcuts/attachments/account/snapshot/CardDAV filter: 109 passed; one Google callback response socket reset (`artifacts/platform-regression-tests.log`). Fixed final browser response completion before listener disposal; affected auth/session filter then passed 14/14 (`artifacts/platform-auth-final-tests.log`).
- `MailCal.Platform.Windows` and local-lab `DatabaseGenerator` Debug x64 build: 0 errors/warnings (`artifacts/platform-lab-build.log`).
- Intelligence console Debug x64 build: 0 errors/warnings (`artifacts/platform-console-build.log`). Console now references the Windows platform target and supplies native DPAPI/auth registrations without loading WinUI.
- New English source translation keys only: credential recovery, generic operation failure and consent version/change strings required by imported shared VMs. Other locales unchanged.


## Integration review corrections

- Frozen legacy Core.ViewModels owns the previous `INativeAppService` and `WindowsTaskbarPosition` source under `Legacy` without changing deprecated UWP application sources. The production Domain/Core/Services/Auth/Mail.ViewModels graph contains neither native handle contract.
- Billing launches use `IExternalLauncher`; Microsoft Store fallback/redemption consults `IPlatformCapabilities`. Windows capability registrations keep existing Store behavior, while macOS uses online account entitlement. Store-unavailable tests imported with the selected capability changes.
- Runtime shutdown awaits actual job/provider cancellation completion. Caller cancellation cancels that caller's wait; it cannot report the runtime stopped prematurely. Non-cancellation teardown failures are logged and surfaced with Faulted state.
- Review regression filter (runtime, Store capability, purchase reconciliation): 26/26 passed (`artifacts/platform-integration-review-tests.log`). Both affected Windows tools rebuilt without warnings/errors after the legacy contract move.
- Release scripts parsed and dot-sourced successfully; release target XML and profile JSON validated. Pure release-plan/profile/argument checks resolve the active WinUI project and preserve Release/x64/win-x64. No release-specific test files were discovered in the tracked repository. No packaging or publication was invoked by these script checks.

## Notification capability integration

Synchronization initialization now consumes the host notification capability. Successful mail synchronization skips native notification/badge publication when the feature is unavailable; calendar badge and credential-attention notification calls use the same gate. The original public initialization signature remains as a compatibility overload with notifications enabled; active host initialization passes real capabilities explicitly. Windows capabilities preserve its existing behavior.
