# Service consolidation plan

Scan date: 2026-09-24. Branch `feature/vNext`.

## Scope of the scan

Every `*Service.cs` under `src/`, plus the non-`Service`-suffixed collaborators
registered in DI (`ApplicationResourceManager`, `PackagedAppEntryLauncher`,
`NavigationServiceBase`, `TaskCompletionSoundPlayer`, the profile-picture jobs).
Generated Outlook/Kiota models are excluded.

Counts today:

| Layer | Registration site | Service-ish classes |
| --- | --- | --- |
| `Wino.Core.Domain/Interfaces` | — | 81 `I*Service` interfaces |
| `Wino.Services` | `ServicesContainerSetup` | ~65 registrations |
| `Wino.Core/Services` | `CoreContainerSetup` | ~30 registrations |
| `Wino.Mail.WinUI/Services` | `CoreUWPContainerSetup` + `App.xaml.cs` | ~40 registrations |

The proposals below remove **23 classes and 20 interfaces** without changing a
single feature. They are ordered by value-to-risk, and each one states the DI
change so nothing breaks at startup.

---

## 0. Findings worth fixing regardless of the merge

These are not cleanup opinions; they are current defects surfaced by the scan.

### 0.1 Two live dialog stacks, two presentation semaphores

`CoreUWPContainerSetup.cs:33` registers `IDialogServiceBase -> DialogServiceBase`
as its own singleton. `App.xaml.cs:591` registers `IMailDialogService ->
DialogService`, and `DialogService` *derives from* `DialogServiceBase`.

So two separate instances of the dialog stack exist. `DialogServiceBase` holds
`private SemaphoreSlim _presentationSemaphore = new(1)`, which is the guard that
serializes `ContentDialog` presentation. Because the field is private and there
are two instances, there are two semaphores — the guard does not serialize
across the two. Eight view models take `IDialogServiceBase`
(`ProviderSelectionPageViewModel`, `PersonalizationPageViewModel`,
`MessageListPageViewModel`, `ApplicationThemeEditorPageViewModel`, …) while the
mail view models take `IMailDialogService`. A dialog opened from each side can
race, which in WinUI throws `Multiple ContentDialogs`.

**Fix (independent of any merge):** register the base interface as a forwarder,
the pattern the codebase already uses for `IContactQueryService`:

```csharp
services.AddSingleton<IMailDialogService, DialogService>();
services.AddSingleton<IDialogServiceBase>(p => p.GetRequiredService<IMailDialogService>());
```

Then delete the standalone `IDialogServiceBase -> DialogServiceBase`
registration. One instance, one semaphore. No consumer changes.

### 0.2 `INativeAppService.IsAppRunning()` is a stub that always returns true

`NativeAppService.IsAppRunning()` (`NativeAppService.cs:53`) is inside
`#if WINDOWS_UWP`, which is not defined for this app, so it unconditionally
returns `true`. It is marked `[Obsolete]`.

Meanwhile `App.xaml.cs:698` has a real `IsAppRunning()`, reached through
`IAppNotificationHandlerHost`. So `AppNotificationHandler` asks the real one and
`ProtocolActivationHandler.cs:34` asks the stub, which always says "running".

**Fix:** delete `IsAppRunning()` from `INativeAppService`; point
`ProtocolActivationHandler` at the `IAppNotificationHandlerHost` implementation
that already answers correctly.

### 0.3 `PinAppToTaskbarAsync()` is an empty method with a live call site

The entire body is commented out (`NativeAppService.cs:207`) and it is
`[Obsolete("Not supported for Win SDK")]`, but `MailAppShellViewModel.cs:1837`
still awaits it. Whatever UI leads to that line does nothing. Remove the member
and the call site, or implement it.

### 0.4 Two services registered and never resolved

- `ReleaseLocalAccountDataCleanupService` — 15 lines, takes two dependencies and
  ignores both, `RunIfNeededAsync()` returns `Task.CompletedTask`, registered at
  `App.xaml.cs:595`, **zero call sites**. Delete the class and registration.
- `PackagedAppEntryLauncher` — registered at `CoreUWPContainerSetup.cs:43`,
  **zero resolutions**. Either it lost its caller in the app-mode-switch rework,
  or it is genuinely dead. Confirm before deleting; if the mode switcher needs
  it, wire it, otherwise remove.

### 0.5 `StoreRatingService` bypasses the default-browser workaround

`StoreRatingService` calls `Windows.System.Launcher.LaunchUriAsync` directly
(two sites). `NativeAppService.LaunchUriAsync` exists precisely because that API
silently no-ops for some schemes in this packaged self-contained host. `ms-windows-store:`
happens to be one of the schemes the comment says still works, so this is latent
rather than broken — but it should route through the one launcher. Proposal 1
fixes it as a side effect.

### 0.6 `ContactPictureFileService` derives from `BaseDatabaseService` and never uses it

It extends `BaseDatabaseService`, takes `IDatabaseService`, never touches
`Connection`, and carries a private `LegacyAccountContactPictureRow` class that
nothing reads. Proposal 7 removes both.

---

## 1. Platform capability → `INativeAppService`

This is the merge you described. The target already exists and is already a
singleton in `Wino.Mail.WinUI`, so everything below is an intra-project move
with no new dependency edges.

**Absorb into `NativeAppService` / `INativeAppService`:**

| Service | Size | What it is | Dependencies |
| --- | --- | --- | --- |
| `ClipboardService` / `IClipboardService` | 18 lines, 1 method | `Clipboard.SetContent` | none |
| `KeyPressService` / `IKeyPressService` | 15 lines, 2 methods | `InputKeyboardSource.GetKeyStateForCurrentThread` | none |
| `StartupBehaviorService` / `IStartupBehaviorService` | 55 lines, 2 methods | `Windows.ApplicationModel.StartupTask` | none |
| `WebView2RuntimeValidatorService` / `IWebView2RuntimeValidatorService` | 32 lines, 1 method | `CoreWebView2Environment` probe | none |
| `ShellUserPresenceStateProvider` / `IUserPresenceStateProvider` | 52 lines, 2 methods | `SHQueryUserNotificationState` P/Invoke | none |
| `TaskCompletionSoundPlayer` / `ITaskCompletionSoundPlayer` | 10 lines, 1 method | delegates to `NotificationSoundPlayer.Play` | none |

All six are stateless, dependency-free wrappers over a single Windows API —
exactly the shape `NativeAppService` already has (`SHAppBarMessage`, `Package.Current`,
`Launcher`, registry reads). None of them can introduce a cycle, because none of
them depends on anything.

**Caveat on `ShellUserPresenceStateProvider`:** its XML comment explicitly says
the P/Invoke is kept separate so notification policy stays unit-testable against
the interface. `NotificationPolicyService` has tests. Keep `IUserPresenceStateProvider`
as an interface (so the fake still works) but let `NativeAppService` implement
it, the same way it already implements `IAppMetadataService`. Registration
becomes a forwarder.

**Deliberately NOT absorbed:**

- `IStoreRatingService` — depends on `IMailDialogService`, which depends on
  `INewThemeService` and `IWinoAccountProfileService`. Folding it in would give
  the platform-capability singleton a transitive edge to the whole shell. See
  proposal 2 instead.
- `IFileService` — not a Windows API wrapper; it does log archiving and
  redaction. See proposal 9.
- `IThumbnailService` — 349 lines with caching, HTTP, and Gravatar/favicon
  logic. Its own domain.

**DI change:**

```csharp
services.AddSingleton<NativeAppService>();
services.AddSingleton<INativeAppService>(p => p.GetRequiredService<NativeAppService>());
services.AddSingleton<IAppMetadataService>(p => p.GetRequiredService<NativeAppService>());
services.AddSingleton<IUserPresenceStateProvider>(p => p.GetRequiredService<NativeAppService>());
// delete: IClipboardService, IKeyPressService, IStartupBehaviorService,
//         IWebView2RuntimeValidatorService, ITaskCompletionSoundPlayer registrations
```

Call sites change from `_clipboardService.CopyClipboardAsync(x)` to
`_nativeAppService.CopyClipboardAsync(x)` — 10 files for clipboard, 5 for key
press, 4 for startup behaviour, 3 for WebView2.

**Also while you are in there:** `INativeAppService.GetFullAppVersion()` and
`IAppMetadataService.AppVersion` return the same string from the same class.
Drop `GetFullAppVersion()` and move its four callers to `AppVersion`. That is
what lets proposal 8 cut the `Wino.Services -> INativeAppService` edge.

**Net: −6 classes, −5 interfaces** (`IUserPresenceStateProvider` survives).

---

## 2. Microsoft Store surface → one `IMicrosoftStoreService`

Three services, all wrapping `Windows.Services.Store.StoreContext`, all
MSIX-only, two of them independently duplicating the HWND plumbing
(`InitializeWithWindow.Initialize(context, WindowNative.GetWindowHandle(mainWindow))`):

| Service | Size | Surface |
| --- | --- | --- |
| `StoreManagementService` | 79 | `HasProductAsync`, `PurchaseAsync` |
| `StoreRatingService` | 132 | `PromptRatingDialogAsync`, `LaunchStorePageForReviewAsync` |
| `StoreUpdateService` | 123 | `HasAvailableUpdate`, `RefreshAvailabilityAsync`, `StartUpdateAsync` |

One class, one `StoreContext`, one `RunOnMainWindowAsync` helper (currently only
`StoreUpdateService` has the correct dispatcher-marshalling version;
`StoreManagementService` does the HWND init on whatever thread calls it).

Dependencies after merge: `IConfigurationService`, `IMailDialogService`,
`IWinoLogger`. No cycle — nothing in the dialog or theme stack asks for the
store.

**DI:** `AddSingleton<IMicrosoftStoreService, MicrosoftStoreService>()`.
Note the lifetime change: `IStoreRatingService` and `IStoreManagementService` are
currently `AddTransient`, `IStoreUpdateService` is `AddSingleton`. Singleton is
correct — `HasAvailableUpdate` is cached state that transient registration would
throw away.

**Net: −2 classes, −2 interfaces.**

---

## 3. What's New → fold the launcher into the window manager

You named this one. Here is what is actually there:

- `WhatsNewService` (119 lines, `Wino.Services`) — reads `Assets\WhatsNew\*.json`,
  tracks last-opened version in `IConfigurationService`. Cohesive; **keep**.
- `WhatsNewWindowLauncher` (33 lines, `Wino.Mail.WinUI`) — "get-or-create a
  `WinoWindowKind` window, apply the theme, activate it." **Merge.**

`WhatsNewWindowLauncher.ShowAsync()` and
`HostedContentPopoutCoordinator.PopOutCurrentContentAsync()` contain the same
twenty lines: `windowManager.GetWindow(kind)` → activate if present, else
`CreateWindow` → `await themeService.ApplyThemeToActiveWindowAsync()` →
`ActivateWindow`. Both carry the same comment explaining why the theme must be
applied before activation.

Put that sequence on `IWinoWindowManager` as
`Task<TWindow> ShowThemedWindowAsync(WinoWindowKind kind, Func<Window> factory, string? name = null)`,
then `WhatsNewWindowLauncher` collapses to two lines at its single call site and
the interface disappears. `HostedContentPopoutCoordinator` (already a static
helper, not a DI service) shrinks too.

Check that `IWinoWindowManager` taking `INewThemeService` does not close a cycle:
`NewThemeService` depends on `IPreferencesService`, `IConfigurationService`,
`IApplicationResourceManager`, and `IUnderlyingThemeService` — not on the window
manager. Safe. If a later change makes it mutual, inject `Func<INewThemeService>`.

**Also:** `WhatsNewService` takes `INativeAppService` for the single call
`GetFullAppVersion()`. That is a `Wino.Services -> shell-platform` edge for one
version string. Switch it to `IAppMetadataService.AppVersion` (proposal 1).

**Net: −1 class, −1 interface, −1 duplicated block.**

---

## 4. Static catalogs → stop making them services

Three "services" that hold no state, do no I/O, and return a hardcoded list:

| Service | Size | Returns |
| --- | --- | --- |
| `AiActionOptionsService` (WinUI) | 46 | 16 translation languages + 7 rewrite modes, from `Translator` |
| `ProviderService` (WinUI, namespace `Wino.Mail.Services`) | 37 | the provider list |
| `FontService` (`Wino.Core`) | 26 | `SKFontManager` families + 8 defaults, behind a `Lazy` |

- **`AiActionOptionsService`** has nothing WinUI about it — it reads `Translator`,
  which lives in `Wino.Core.Domain`. Move it to `Wino.Core.Domain` as a static
  `AiActionCatalog`. Four call sites.
- **`ProviderService`** depends only on `IKnownImapProviderCatalog`, which lives
  in `Wino.Services`, yet the class sits in the WinUI project. Fold
  `GetAvailableProviders`/`GetProviderDetail` onto `IKnownImapProviderCatalog`
  itself — that interface already exposes `SetupProviders`, which is where the
  data comes from. Eight call sites.
- **`FontService`** is already effectively static (`static readonly Lazy<List<string>>`);
  the instance and the interface add nothing. Either make it static, or if you
  prefer keeping the seam for tests, leave it — it is the weakest of the three.
  Recommendation: make it static, since no test substitutes it today.

**Net: −3 classes, −3 interfaces, −3 DI registrations.**

---

## 5. Context menus → one `IContextMenuItemService`

`ContextMenuItemService` (239 lines, folder + mail-item + render menus) and
`CalendarContextMenuItemService` (116 lines, calendar-item menu) are the same
thing for different entities: pure builders over `Translator` and enums, zero
dependencies, both `AddTransient`. Merge into one class with four methods.

**Net: −1 class, −1 interface.**

---

## 6. Server connectivity tests → one `IMailServerTestService`

`ImapTestService` (69) and `Pop3TestService` (59) both take a
`CustomServerInformation` and answer "can I connect and authenticate." Same
domain, same caller (account setup), same shape. Merge:
`TestImapAsync` / `TestPop3Async` on one interface.

Do **not** pull in `IAutoDiscoveryService` (698 lines) — discovering settings and
validating settings are different jobs with different failure semantics.

**Net: −1 class, −1 interface.**

---

## 7. Picture storage → one `IPictureStorageService`

`ContactPictureFileService` (69) and `AccountProfilePictureFileService` (137)
are the same service twice:

```
GetXPicturePath(Guid) / GetXPictureUri(Guid) / SaveXPictureAsync(byte[]) / DeleteXPictureAsync(Guid)
```

Both write a Guid-named JPEG into an app-data subfolder and hand back an
`ms-appdata:///local/...` URI. Differences: the account one normalizes through
SkiaSharp to 48×48 and supports atomic replace; the contact one writes raw
bytes and (per 0.6) drags an unused `BaseDatabaseService` base along.

Merge into one service keyed by a `PictureKind { Contact, AccountProfile }`
that selects the subfolder, the filename format (`{guid}` vs `{guid:N}`, keep
each as-is so existing files still resolve), and whether to normalize. Contact
pictures get the atomic write for free.

**Watch the filename formats.** `{fileId}.jpg` (contacts, with dashes) and
`{fileId:N}.jpg` (accounts, without) are not interchangeable — carry both
through the merge or existing pictures go missing.

**Also merge the two one-shot startup jobs:** `AccountProfilePictureMigrationService`
(66) and `AccountProfilePictureBackfillService` (68) each have exactly one call
site, adjacent lines in `App.xaml.cs` (735 and 756). One
`AccountProfilePictureMaintenance` with `MigrateLegacyAsync()` and
`BackfillAsync()`.

**Net: −3 classes, −1 interface.**

---

## 8. MIME storage → fold `IMimeStorageService` into `IMimeFileService`

`MimeStorageService` (111 lines) is a thin layer over the MIME root: root path,
per-account sizes, per-account delete, delete-older-than.
`IMimeFileService` (361 lines) already owns `GetMimeResourcePathAsync`,
`IsMimeExistAsync`, `DeleteMimeMessageAsync`, and `DeleteUserMimeCacheAsync`
over the same directory tree. Two services owning the same folder is how the
folder layout drifts.

Merge the four methods into `IMimeFileService`.

**Lifetime note:** `IMimeStorageService` is `AddTransient`, `IMimeFileService`
is `AddSingleton`. Singleton is fine — `MimeStorageService` holds no per-call
state.

**Cross-dependency note:** `MimeStorageService` takes `INativeAppService` purely
for `GetMimeMessageStoragePath()`. See proposal 11.

**Net: −1 class, −1 interface.**

---

## 9. Activation state holders → one `IActivationStateService`

`LaunchProtocolService` is **10 lines** — two auto-properties, no logic.
`ShareActivationService` (70 lines) is the same idea with a lock: hold what the
app was activated with until the shell consumes it.

Merge into `IActivationStateService`: launch parameter, mailto URI, pending
share request, pending compose share request. One place to look when asking
"what did activation hand us."

`IActivationFileImportService` (1008 lines) is real import work — keep separate,
but move the three files into a common `Activation/` folder in `Wino.Services`.

**Net: −1 class, −1 interface.**

---

## 10. Intelligence entitlement → fold into the snapshot service

`WinoIntelligenceEntitlementService` (109) is a cached-snapshot facade over
`WinoAccountIntelligenceSnapshotService` (202): `Current`, `GetAsync`,
`RefreshAsync`, `SetSignedOut` against the snapshot service's
`GetCachedAsync`/`RefreshAsync`/`ClearAsync`. Same data, same lifecycle, two
singletons. Merge.

Likewise `IWinoPendingCheckoutStore` (33 lines, get/set/clear a product type per
account) belongs inside `IWinoBillingService` — it exists only to be read by the
reconciliation service after a checkout.

**Explicitly do NOT merge the rest of the Wino-account cluster.**
`IWinoAccountSessionService`, `IWinoAccountProfileService`, `IWinoBillingService`
and `IWinoPurchaseReconciliationService` form a deliberate layering: session
generation → profile → billing → reconciliation. `WinoPurchaseReconciliationService`
already is the coordinator. Collapsing any two of them creates the constructor
cycle you are worried about (billing needs profile, profile needs session,
reconciliation needs all three).

**Net: −2 classes, −2 interfaces.**

---

## 11. App-data paths → one owner (`IApplicationConfiguration`)

Today, "where does X live on disk" is answered in four places:

- `IApplicationConfiguration.ApplicationDataFolderPath` / `ApplicationTempFolderPath`
- `INativeAppService.GetMimeMessageStoragePath()` (creates `LocalFolder\Mime`)
- `INativeAppService.GetCalendarAttachmentsFolderPath()` (creates `LocalFolder\CalendarAttachments`)
- each file service building its own subfolder inline
  (`contacts`, `account-profile-pictures`, `Assets\WhatsNew`)

Because the MIME and calendar-attachment paths sit on `INativeAppService`, three
`Wino.Services` types (`MimeStorageService`, `MimeFileService`, `WhatsNewService`)
take a dependency on the shell platform service to learn a folder name.

Move both path methods onto `IApplicationConfiguration` as
`MimeStorageFolderPath` and `CalendarAttachmentsFolderPath`. `ApplicationConfiguration`
is 16 lines in `Wino.Services` and is initialized at startup from the WinUI layer,
which is exactly where `ApplicationData.Current.LocalFolder` is known.

This is the highest-value *cross-dependency* change in the document: it is what
lets `Wino.Services` stop referencing `INativeAppService` at all, which is the
direction of the layering. Do it together with proposals 1, 3 and 8.

**Net: 0 classes removed, 3 unnecessary layer-crossing edges removed.**

---

## 12. `NavigationServiceBase` → fold into `NavigationService`

17 lines, one method (`GetNavigationTransitionInfo`), one subclass, not
registered in DI, no other implementor. It is a base class for the sake of
being a base class. Inline it into `NavigationService` as a private static.

**Net: −1 class.**

---

## Optional / judgment calls (not recommended for this pass)

- **`ISignatureService` (57) + `IEmailTemplateService` (42)** — both are 5-method
  CRUD over one table, both are "canned content you drop into a draft." A
  `IComposerAssetService` would work, but signatures are per-account with a
  default-creation path and templates are global. Defensible either way; low
  payoff. Leave.
- **`IContactService` / `IRecipientHistoryService` / `IRecipientSuggestionService`** —
  these are new on this branch and are correctly layered (storage → history →
  ranking). `IRecipientSuggestionService` (140) composes the other two. Leave.
- **`IStatePersistanceService` vs `IPreferencesService` vs `IConfigurationService`** —
  three settings-shaped interfaces, but genuinely three things: transient UI
  state, exported user preferences, and raw key/value storage. The distinction
  is load-bearing (`WhatsNewService` documents *why* it uses config rather than
  preferences). Leave. Do fix the typo'd interface name `IStatePersistanceService`
  if you are touching it anyway — the file is `IStatePersistenceService.cs`.
- **`IAccountCapabilityService` / `IAccountProviderFeatureService` /
  `IProviderFeatureAuthorizationService`** — three services around
  `ProviderFeature`, which looks mergeable, but they sit on different sides of a
  layer boundary: the storage one is in `Wino.Services`, the authorization and
  capability ones are in `Wino.Core` with synchronizer dependencies. Merging
  would pull `Wino.Services` up into `Wino.Core`. Leave.
- **`MailService` (3048 lines)** — the opposite problem. Out of scope here, but
  worth its own splitting pass later.

---

## Summary

| # | Merge | Classes | Interfaces |
| --- | --- | ---: | ---: |
| 0.4 | Delete dead registrations | −2 | 0 |
| 1 | Platform capability → `INativeAppService` | −6 | −5 |
| 2 | Store surface → `IMicrosoftStoreService` | −2 | −2 |
| 3 | What's New launcher → `IWinoWindowManager` | −1 | −1 |
| 4 | Static catalogs → static classes / existing owners | −3 | −3 |
| 5 | Context menus → one service | −1 | −1 |
| 6 | IMAP/POP3 tests → one service | −1 | −1 |
| 7 | Picture storage + profile-picture jobs | −3 | −1 |
| 8 | MIME storage → `IMimeFileService` | −1 | −1 |
| 9 | Activation state → `IActivationStateService` | −1 | −1 |
| 10 | Intelligence entitlement + checkout store | −2 | −2 |
| 12 | `NavigationServiceBase` → `NavigationService` | −1 | 0 |
| | **Total** | **−24** | **−18** |

Plus proposals 0.1–0.3, 0.5, 0.6 and 11, which fix defects and cut three
layer-crossing dependencies without removing anything.

## Suggested order

Each step builds and ships on its own.

1. **Defects first** — 0.1 (dialog semaphore), 0.2, 0.3, 0.4. Small, and 0.1 is
   a real crash risk.
2. **Paths** — 11. Do this before 1, 3 and 8 so those merges land on the final
   shape.
3. **Platform** — 1, then 12. Biggest single reduction, lowest risk, no new edges.
4. **Leaf merges** — 4, 5, 6, 9. Independent of each other, mechanical.
5. **Storage merges** — 7, 8. Care needed on the filename formats in 7.
6. **Shell/account** — 2, 3, 10. Touch DI lifetimes; verify startup after each.

## DI safety checklist for every step

- Nothing here introduces a constructor cycle. The two places where one is
  possible are flagged inline (proposal 3's window-manager → theme edge, and the
  Wino-account cluster in 10, which is why that cluster stays split).
- Where a class implements several interfaces, register the concrete type once
  and forward each interface with
  `AddSingleton<IFoo>(p => p.GetRequiredService<Concrete>())`. The codebase
  already does this for `IContactQueryService`, `ITaskQueryService` and
  `IAppMetadataService`; keep it consistent, or you get two instances (which is
  exactly bug 0.1).
- Three merges change a lifetime from transient to singleton (2, 6, 8). Confirm
  the merged class holds no per-call mutable state before flipping.
- After each step, run the app once to the shell. A missing registration throws
  at first resolve, not at build.
