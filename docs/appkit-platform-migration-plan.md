# Windows abstraction import and native AppKit migration

Status: **platform foundation built and verified; UI implementation and planning stopped by the user on 7 October 2026**. Full presentation parity will be planned in a separate task. See [the verification and handoff boundary](harness/appkit-foundation-validation.md).

Inspected on 6 October 2026. Destination: `D:\Wino-Mail`, HEAD `40bae46a`; donor: `D:\Wino Mail Uno\Wino Mail Uno Platform`, HEAD `62858db`. Both worktrees were clean before planning. These identifiers are provenance, not instructions to reset either checkout. Recheck both before implementation.

This accepted plan supersedes the Uno architecture and completion scope in [the earlier plan](windows-abstraction-and-macos-implementation-plan.md). Implementation follows the confirmed decisions below. The donor is a source of selected changes and contract evidence, not a second maintained application or a repository to merge wholesale.

## 1. Outcome and non-negotiable constraints

The Windows app continues using WinUI 3. A new `Wino.Mail.MacOS` application uses native AppKit in C# through the .NET macOS workload and directly consumes the existing shared .NET services and ViewModels. No Uno, MAUI, Catalyst, Swift/JSON ViewModel bridge, or new XAML-to-AppKit framework is introduced.

The following decisions were explicitly established in the referenced conversations:

| Decision | Implementation requirement |
| --- | --- |
| Preserve Windows | Preserve the WinUI head, Windows workflows, storage formats, package identity, publisher, authentication and release behavior. Every imported abstraction is wired to a real Windows implementation in the same batch. |
| One shared ViewModel library | Consolidate Core/base, mail, calendar, contacts, tasks, settings and shell ViewModels into `Wino.Mail.ViewModels`. Keep exactly two TFMs: `net10.0` and `net10.0-windows10.0.26100.0`. macOS project references consume `net10.0`; Windows consumes the Windows variant with its CsWinRT/AOT exposure. No separate Core, Calendar or Shell ViewModel project and no Mac-specific duplicate of a production ViewModel. |
| Full parity is the destination | Port existing screens, controls, workflows and shared ViewModel behavior. Foundation first, followed by the detailed parity roadmap. Printing/PDF, general file/protocol activation and global hotkeys are explicitly deferred until after the working prototype; Microsoft Store purchase UI is excluded on Mac. Other native integrations remain required. Source presence and disabled adapters are not feature completion. |
| Purchases | macOS uses Wino Account entitlement/purchase behavior. Microsoft Store purchase UI is excluded on Mac; do not introduce a separate Mac purchase ViewModel or infer a new StoreKit purchasing feature. |
| Simple C# UI authoring | Typed binding and layout helpers hide subscriptions, dispatch and cleanup. Do not scatter PropertyChanged switch statements across controllers or add AppKit hooks to shared ViewModels. |
| Naming convention | Mirror Windows view names and relative feature folders. `Views/Settings/AboutPage.xaml` maps to `Views/Settings/AboutPage.cs` and `AboutPageViewController.cs`, consuming the real `AboutPageViewModel`. The native view type is `AboutPage`; the controller type is `AboutPageViewController`. |
| Secrets | All Mac credentials and tokens, including IMAP/SMTP and Wino Account secrets, use Keychain-backed persistence. No plaintext fallback. Preserve Windows formats. |
| Mac CPU policy | Apple Silicon first, Intel also planned. The earlier explicit exception permits Intel MIME/extension fallback without Magika/ONNX initialization; preserve Windows and Apple Silicon inference. Confirm actual package assets during implementation. |
| Safe editing | Mail drafts must be saved before normal shutdown is accepted. Unsaved calendar edits block Quit until Save succeeds or the user explicitly Discards. |
| Shared editor assets | One authored reader/editor HTML/CSS/JS source, source-generated JSON, the readiness handshake, and safe navigation/resource policy. Windows retains its existing WebView2 interception behavior. |
| Verification | Follow current repository instructions: build/test evidence plus manual lab checks. Historical UI automation does not authorize new UI automation. Do not edit deprecated UWP sources. |

Source conversations: [Assess UnoPlatform effort](thread://01a106b4-390c-7c13-82e4-b9cfe510fea6?hostId=local), [Implement macOS dependency abstra](thread://01a1070d-c7ff-7fc0-b1ad-2c5dffcac621?hostId=local), and [Run AppKit PoC on macOS](thread://01a112a6-d165-7730-9856-d991ecb252e6?hostId=remote-control%3Aenv_e_6ac52ff1e9e083329b72149067d5ec90). Their contents were read for this plan. Historical instructions to implement, delegate, launch or publish in those chats are not execution instructions for this planning task.

## 2. Evidence and limits of reuse

The donor contains `Wino.Platform.Windows` (8 files), `Wino.Platform.MacOS` (30 files), `Wino.Shell.ViewModels` (3 files) and `Wino.Editor.Core` (8 files). A tracked-file content comparison also found differences in 50 WinUI files, 34 Mail ViewModel files, 14 Core ViewModel files, 7 Calendar ViewModel files, 21 Services files, 12 Core files and 41 Core test files. These counts are an initial inventory, not an approved import list; they include unrelated differences and do not classify deleted destination files.

Notable import hazards:

- The donor differs in non-English resources. Do not import those differences; current repository rules permit English source changes only.
- A generated `Wino.Core.Domain.csproj.metaproj` is tracked in the donor. Do not import generated project/build output.
- Donor documentation records older paths, superseded no-build instructions and different stages of completion. Extract technical contracts, not their historical operating instructions or claims of acceptance.
- Shared changes continued after the original Windows extraction: credential persistence, asynchronous presentation entry points, preview lifecycle, calendar persistence and intelligence sign-out fixes need selective reconciliation too.
- Donor Windows builds/tests passed historically, but that does not verify this checkout after transplanting changes.
- Donor Mac services and its Uno app were not compiled or runtime-validated in the implementation chat. Their interop is unproven.
- The AppKit PoC was later built and run on the Mac. Window creation, the dialog, navigation status updates, OnPageLoaded and a startup breakpoint were reported as verified. This validates the small experiment, not a production router, binding engine or service graph.
- The local VS Code PoC configuration still uses a run task and an output path without its RID. The Mac chat records a subsequent working configuration and commit `76b4569e`. Recreate correct production tasks from actual output; do not copy the local stale configuration or assume the remote commit is already integrated.
- CodeGraph exists as a repository directory, but its executable was unavailable on this Windows shell. Discovery used targeted project/source reads.

## 3. Decision ledger

The user confirmed D1–D6 and D10–D12 during this planning conversation. Recommendations do not silently resolve the remaining questions.

| ID | Confirmed decision | Implementation consequence |
| --- | --- | --- |
| D1 | Foundation/import first, with a detailed full-parity roadmap. | Do not implement the whole UI as part of the foundation task or call the foundation the completed port. Prototype depth is D7 below. |
| D2 | Include everything except printing/PDF, general file/protocol activation and global hotkeys; those follow the working prototype. Store purchase UI remains excluded. | Notifications/actions, S/MIME, startup, background/tray/companion native equivalents and extra windows belong in the full-parity roadmap. OAuth callbacks remain mandatory even while general activation is deferred. |
| D3 | Direct distribution first; Mac App Store publication later. | Apple signing is required for distributed Mac releases; Azure Windows signing cannot substitute. App sandbox timing is D9 below. |
| D4 | macOS 14, subject to workload/native-dependency validation. | Validate the pinned SDK/workload/Xcode/dependencies before promising the deployment minimum. Raise an incompatibility explicitly; do not silently increase it. |
| D5 | Keep running and syncing after the last window closes; Dock reopen; Command+Q safely quits. | Runtime is process-owned, not main-window-owned. Closing a dirty compose window still follows draft-safety rules. |
| D6 | Preserve screen structure, settings and workflows; use native Mac presentation. | Native controls, menus and sheets are appropriate; no silent omission of fields, actions or states. |
| D10 | All application ViewModels belong in one `Wino.Mail.ViewModels` library with portable and Windows TFMs. | Merge existing Core/Calendar ViewModels and donor shell code into this project. Keep modes organized by folders rather than separate assemblies. This supersedes the earlier proposed Shell project. |
| D11 | No AppKit playground application. | Verify native Mac controls, bindings and editor integration through focused tests and manual scenarios in `Wino.Mail.MacOS`. Keep the existing Windows playground. |
| D12 | `Wino.Platform.Windows` is designed for the WinUI head; no future Win32 or WPF host support is planned. | WinUI/Windows App SDK dependencies and Windows targeting are allowed. Existing tools do not impose a plain-.NET or UI-framework-neutral compatibility requirement on this library. |

Foundation decisions confirmed in the implementation request:

| ID | Choice to confirm | Proposed interpretation |
| --- | --- | --- |
| D7 | OAuth/sidebar prototype | Native WelcomeWindow and WelcomePageV2 using production shared ViewModels, OAuth account setup, then the shell with real folder navigation. IMAP setup is deferred. Reader/compose/send workflows remain later parity work. |
| D8 | Production identity/data | Retain `com.winomail.desktop`. Experimental data does not require migration; do not erase it. Keychain and sandbox storage identities stay stable. |
| D9 | App Sandbox from the beginning | Sandbox container roots, client networking, loopback OAuth server entitlement, user-selected file access and Keychain persistence are foundation requirements. Hardened runtime/signing/notarization belong to distribution validation. |

Signing finding: Azure Artifact Signing issues Authenticode signatures through SignTool; it is not an Apple Developer ID identity. For direct Mac distribution, sign native code/app and the DMG with an Apple Developer ID Application identity, use the required hardened runtime/timestamp configuration, submit for notarization and staple/validate the result. A PKG installer, if later chosen, uses Developer ID Installer. App Store distribution is a separate later channel. Apple certificate availability is a release prerequisite, not a blocker to local foundation development. Sources: [Microsoft Artifact Signing FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq), [Apple Developer ID](https://developer.apple.com/developer-id/), [Apple packaging guidance](https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution).

Before committing to a release pipeline, resolve the Mac runtime/trimming/AOT mode with a build spike on the pinned workload. `IsAotCompatible` in common props is not a decision to ship Mac Native AOT. Windows Native AOT remains preserved regardless. Signing credentials and final installer format can follow later, but the distribution/sandbox contract cannot.

## 4. Proposed project layout and dependency rules

Use the singular `Wino.Platform.*` to match the existing donor names and Wino namespace conventions. Use framework names for presentation libraries (`WinUI`, `AppKit`) and OS names for OS services (`Windows`, `MacOS`). This leaves room for a future platform without pretending native controls themselves are portable.

| Project/directory | Action and ownership | Target/reference rule |
| --- | --- | --- |
| `src/Wino.Mail.WinUI` | Keep Windows composition, pages, app-specific navigation, dialogs, windows, tray and activation. | Existing Windows target and package identity. References Windows platform and WinUI presentation only. |
| `src/Wino.Mail.MacOS` | Create production AppKit executable, app delegate, windows, typed route factory, screen views/controllers and composition. | `net10.0-macos`; selected CPU RIDs. References Mac platform/AppKit presentation and portable shared targets. |
| `src/Wino.Platform.Windows` | Windows implementation layer for `Wino.Mail.WinUI`: DPAPI, broker/cache, attachment policy, certificates and appropriate WinUI-dependent platform services. No requirement to support another Windows UI framework. | Windows-targeted; WinUI/Windows App SDK dependencies are allowed. Do not preserve donor plain `net10.0` targeting for hypothetical reuse or existing tool convenience. Never reference from the Mac or portable shared graph, and never reference the application head back from this library. |
| `src/Wino.Platform.MacOS` | Adapt Keychain, protection, paths/preferences, auth, launch/quarantine, clipboard, startup/notification OS services. | Prefer `net10.0-macos` for native .NET bindings. It does not reference either app head or reusable UI controls. |
| `src/Wino.Mail.ViewModels` | Sole application ViewModel assembly: merge Core/base, Calendar, all four modes, settings and donor shell composition. | `net10.0;net10.0-windows10.0.26100.0`. CsWinRT and `WINRT_EXPOSED` apply only to the Windows target. Native Mac projects select the portable target. |
| `src/Wino.Core.ViewModels`, `src/Wino.Calendar.ViewModels` | Retire after consolidation and consumer updates. Do not create `src/Wino.Shell.ViewModels`; import its useful donor source directly into Mail.ViewModels. | Remove obsolete project/solution references and inter-ViewModel project edges. No compatibility forwarding assemblies as a permanent replacement for consolidation. |
| Existing domain/services/auth/core/messaging libraries | Reconcile portable interfaces, preferences/runtime logic and proven necessary lifecycle changes. These business libraries remain separate from ViewModels. | No Uno, AppKit, WebView2, native handles or platform-default registrations in portable business contracts; no dependency back to Mail.ViewModels. |
| `controls/Wino.Mail.Controls.Core` | Keep deterministic projection, grouping, selection and reusable models. Extend only for demonstrably shared behavior. | Preserve portable and Windows CsWinRT variants. No native control hierarchy abstraction. |
| `controls/Wino.Mail.Controls.WinUI` | Rename current `Wino.Mail.Controls` project/directory to make ownership explicit. Preserve behavior. | Windows-only. Initially preserve public CLR namespaces where useful to reduce XAML churn; audit assembly-qualified resource references. |
| `controls/Wino.Mail.Controls.AppKit` | Create native reusable controls using the same Core contracts. | `net10.0-macos`; no application service graph. |
| `controls/Wino.Presentation.AppKit` | Create small general binding, lifetime, dispatch-to-view and Auto Layout helpers used by controls and screens. | AppKit-specific; no references to Wino Mail ViewModels or head. Reuse the existing dispatcher contract through adapter/delegate injection rather than inventing a competing dispatcher. |
| `controls/Wino.Editor.Core` | Import portable sessions, DTOs, JSON context, operation queue and document policies. Become sole asset embedding owner. | `net10.0`; no native browser objects or application business services. |
| `controls/Wino.Editor.WinUI` | Rename existing native editor project; consume Core while retaining WebView2 controls/interception/printing. | Windows-only. Keep intentional Windows-only public APIs outside Core. |
| `controls/Wino.Editor.AppKit` | Create WKWebView session/control implementations with shared assets/contracts. | `net10.0-macos`; native delegates and message handlers owned/disposed here. |
| `controls/Wino.Mail.Controls.Playground.WinUI` | Rename existing playground and update references/tasks. | Windows sample host; preserve existing scenarios. |
| `tests/...` | Retain current suites; add focused pure binding/lifetime and Mac adapter checks where supported. | Pure logic tests run without native UI; native checks run on Mac; UI acceptance stays manual. |

`Wino.Platform.Windows` serves the WinUI application. Its boundaries should simplify the actual Windows implementation, not anticipate WPF or a separate Win32 application. It may own WinUI-dependent platform adapters; retain app-specific screen composition and route/window orchestration in the head where they require application types. Shared interfaces stay portable, while their Windows implementations may use native handles and WinUI types internally.

The intelligence console and local-lab database generator are existing maintenance consumers to reconcile, not additional supported application heads. Audit their concrete adapter needs during import. They may use a compatible Windows-targeted reference if viable, or narrowly scoped tool-owned composition where necessary; do not create another general Windows abstraction project or retain a portable target solely for them. Preserve their current functionality and verify any target/runtime changes, without making them initialize the WinUI application to access non-UI operations. Focused tests and the Windows playground are verification consumers, not reasons to promise multi-framework support.

Do not create an AppKit playground or a replacement standalone Mac sample application; native UI verification belongs in `Wino.Mail.MacOS`, with focused tests for independently testable behavior.

Do not create a new universal `Wino.Platform.Core` duplicating Domain interfaces. Introduce another neutral library only if a real dependency cycle or independent consumer justifies it. Pure binding state machinery can be tested independently within a small neutral library if tests demonstrate that need; do not design a cross-platform UI framework in advance.

### ViewModel consolidation procedure

Organize the unified library into feature folders such as `Common`, `Shell`, `Mail`, `Calendar`, `Contacts`, `Tasks` and `Settings`. Move base classes, shared presentation helpers and mode providers with their consumers. Preserve public type names and the page-to-ViewModel convention; resolve duplicate filenames using feature folders. Namespace cleanup is a separate mechanical step if needed, not a reason to retain extra projects.

Audit the union of package/project dependencies, source-generator inputs, conditional WinRT annotations, partial classes, internal visibility and resource ownership before moving files. Remove references from Mail.ViewModels to the retired projects rather than introducing a self-reference. Keep domain/service libraries independent of the consolidated presentation assembly.

Update every active app, test, tool and solution reference, DI registration, using directive, XAML namespace and assembly-qualified type/resource reference affected by the move. Preserve existing test coverage without requiring test-project consolidation. Do not modify deprecated UWP projects; inspect their references and report any legacy breakage or conflicting retention requirement before removing a project they still require.

Retain the two declared TFMs and normal compatible ProjectReference selection: a `net10.0-macos` consumer uses `net10.0`, while WinUI uses the Windows target. Scope `Microsoft.Windows.CsWinRT`, `WINRT_EXPOSED` and Windows RID/build settings to Windows builds. Portable restore/build must not inherit the current unconditional Windows RuntimeIdentifiers or require native Windows UI assemblies. Verify evaluated restore/build graphs on both hosts; selecting a compatible TFM for compilation alone does not establish restore isolation. Do not add a Mac-specific ViewModel TFM or remove the Windows target to bypass build problems.

Consolidation acceptance requires both ViewModel targets to build, directly affected VM tests to pass, and the Windows head's Debug build to pass. Because the assembly boundary and generated WinRT exposure change, also verify the Windows Release/AOT path without launch. On Mac, verify that the production head resolves only the portable ViewModel output. Runtime/manual acceptance remains separate from compilation.

Proposed final asset source: `controls/Wino.Editor.Core/Editor`. Move the existing asset tree once, byte-for-byte initially, and retain explicit existing logical resource names (`Wino.Editor.Assets.Editor.*`). This is a deliberate change to the current canonical path; update [controls guidance](../controls/AGENTS.md), [implementation rules](harness/implementation-rules.md), bundling scripts, tests and playground instructions in the same batch. Until that batch is accepted, `controls/Wino.Editor/Editor` remains canonical. No duplicate authored assets during transition.

The naming moves and behavior extraction are separate batches. Check assembly names, `ms-appx` URIs, XAML xmlns, Generic.xaml loading, icon-font generation destinations, editor embedding, release manifests, script paths and docs. Do not infer that moving a csproj and fixing ProjectReference entries is sufficient.

## 5. Donor import map

| Area | Reuse/adapt | Recreate or reject | Evidence needed |
| --- | --- | --- | --- |
| Protection | `ISecretProtector`, DPAPI adapter, DAV/intelligence seams and fixtures. | Any Windows blob/schema migration introduced only for symmetry. | Existing bytes, entropy, scope and paths round-trip unchanged. |
| Auth/credentials | `IGoogleTokenStore`, `IOutlookAuthenticationHost`, `IAccountCredentialPersistence`, platform policies, recovery metadata and affected callers. | Shared HWND sentinel, implicit Windows broker defaults, plaintext Mac fallback. | Interactive/silent login, expiry, restart, removal, cancellation and write failure tests. |
| Preferences/resources | Portable `PreferencesService`, generator ownership, resource resolver, explicit paths-before-DI ordering. | Wholesale donor translations and incidental preference behavior changes. | Same Windows keys/defaults/export exclusions and URI behavior. |
| Runtime/shell | `ApplicationRuntime`, initialization, shell VM/resolver, scheduling and lifecycle tests. | Uno application/window objects and host delegate proxy. | One scheduler/subscription graph, retry/cancel/stop semantics, shell dispatcher ownership. |
| Native operations | Launcher, clipboard, shortcuts, attachment, print/certificate/capability contracts. | Capability-disabled features claimed as full parity; Windows native types in portable APIs. | Windows operations remain enabled, invalid/unavailable/cancelled states are distinguishable. |
| Editor core | Sessions, queue, source-generated serialization, canonical assets and portable document policy. | Uno WebView host, global browser state, assumptions that navigation events intercept every resource. | Ready ordering, stale generation rejection, disposal, resource-policy fixtures. |
| Mac storage | Keychain/protection policies, credential revision strategy, application paths and atomic nonsecret settings. | Blind reuse of raw native interop without validation; arbitrary replacement of missing keys. | Native Keychain denial/loss/rebuild/restart tests; no secrets in logs or SQLite on Mac. |
| Mac lifecycle | Save/freeze/abort ordering and calendar Save/Discard rule. | `MacApplicationTerminationGuard`, `termination.m`, Uno runtime-version checks and delegate forwarding. | Native delegate-owned delayed termination, cancelled quit restores usable views. |
| Mac native helpers | Reuse behavior requirements for clipboard, quarantine, keys. | Uno keyboard hook and Objective-C helper dylib where .NET native APIs cover the operation. | Main-thread correctness, ownership, native errors and physical Ctrl/Cmd tested. |
| Mac UI | Use donor screen inventories and shared VM changes as coverage/reference. | All Uno XAML, Uno route classes, Uno controls, package pins, runtime and build targets. | Implement native screens against current Windows behavior, not donor enabled-only subset. |
| PoC | Preserve naming, translator use, typed bindings, dispatcher and lifecycle lessons. | Sample ViewModel/dialog service as production business logic, fixed-frame layout as default, hidden `new Dispose()` teardown pattern. | Production ownership/disposal and real shared-VM scenario on Mac. |

Before editing, generate a bidirectional file manifest: source-only, destination-only, changed and identical. Normalize text for comparison so newline-only differences are not treated as behavior changes. Compare common history if available, otherwise manually reconcile destination evolution. Record each selected file/hunk and its reason. Import tests with the behavior they protect. Never bulk-copy `src`, `controls`, root build files or the donor Git history.

## 6. Windows compatibility requirements

Retain DAV DPAPI CurrentUser and entropy derived from `Wino.DAV.{accountId:D}`; retain intelligence key context `Wino.IntelligenceResultKey.{winoUserId:D}` and current key/database semantics. Preserve Outlook cache, Gmail token JSON, Windows IMAP/SMTP/Wino Account database behavior and legacy migration gates. The Mac policy must not rewrite Windows secrets into Keychain-reference fields.

Move Microsoft broker/ProtectedData/COM implementation dependencies out of portable execution paths. Package metadata and a plain TFM alone do not prove portability. Shared services must be constructed by explicit host composition; test doubles must not mask missing registrations. Update the intelligence console and lab generator along with the main head. Keep NotificationHost's package activation contract intact.

Preserve Windows print settings, reader policy, certificate/private-key behavior, notification actions, tray reopening, startup, global shortcuts and Store behavior. Optional-feature capability checks must not hide Windows actions after import. No default-success no-op adapters.

Keep existing singleton/transient ownership and scheduling semantics unless a separately justified defect requires change. In particular, moving shell code must not dispose provider singletons on transient controller teardown or duplicate sync loops. Preserve published unconditional PackageReferences; no sibling projects or local feeds.

## 7. AppKit infrastructure specification

### 7.1 Composition, ownership and lifecycle

Build one shared service provider per process after identity, paths, protection and translator initialization. The head owns the runtime, route registration and application/window lifecycle. Each window/presentation owns its controller/binding scope; shared services remain process-owned. Do not obtain dependencies through a new global service locator.

The production factory supplies the existing ViewModel and assigns its dispatcher before initialization, notifications or collection changes. Controllers do not construct fake versions of application services. Keep route-to-controller factories explicit and typed. A base `WinoViewController<TViewModel>` handles common lifetime transitions without adding platform methods to shared ViewModels.

Required state semantics: created -> view loaded/bound -> active -> inactive/cached or released. The router, not incidental native appearance callbacks, decides logical activation. Call `OnNavigatedTo` once per logical entry; `OnPageLoaded` according to the audited Windows page contract; `OnNavigatedFrom` once per logical exit. Native hide/show, covering sheets and re-layout must not duplicate recipient registration. Audit each special existing page before selecting cache/disposal behavior.

Async initialization and pending persistence have an observed task and cancellation/lifetime owner. A route generation prevents late completion from mutating a replaced screen. Navigation failure must leave either the prior route usable or an explicit retry state. Audit the donor's new awaitable VM entry points before importing them; preserve Windows callers.

Permanent release cancels owned work, detaches bindings and native delegates, unregisters recipients and disposes only owned objects. Await editor/persistence cleanup before releasing their native hosts. Integrate with `NSViewController`/NSObject disposal rather than hiding `Dispose()` as the sample does. No finalizer-based draft saving and no assumption of a universal existing VM `Destroy` method.

### 7.2 Binding and layout helpers

Freeze the following helper behavior before porting dozens of screens:

| Concern | Required semantics |
| --- | --- |
| One-way/one-time | Initial value; typed getters; all-properties notifications (`null`/empty); null/default mapping. One-time translator bindings do not pretend Translator raises changes. |
| Two-way | Explicit setter, equality/reentrancy guard and commit trigger appropriate to text, numeric, date and selection inputs; preserve IME composition and validation. |
| Nested paths | Either explicit supported typed path observation with child replacement/unsubscription, or reject unsupported expressions clearly. Do not accept a leaf name and silently miss parent replacement. |
| Commands | Parameters, `CanExecuteChanged`, recheck on invocation; async busy/cancellation/error policy consistent with current shared commands. Buttons, menu items and keyboard routes share command state. |
| Dispatch | Marshal native updates to main thread; reject stale work after disposal/rebind. Do not block the main thread waiting for queued work or hold presentation locks across dispatch callbacks. |
| Collections | Observe collection changes and item changes; stable identity, selection, grouping, reset, reorder, paging and row reuse. Batch native updates; no full list rebuild for every property event. |
| Lifetime | A disposable binding scope per view and reusable row; rebind unsubscribes the previous item; queued callbacks cannot retain/revive a released controller. |
| Diagnostics | Fail clearly for unsupported bindings; log through existing logging boundaries without mail content or secrets. |

Use Auto Layout/stack/split helpers for native layout, with explicit constraints and priorities. Fixed frame coordinates in the PoC are demonstration code. Helpers should express common Wino layouts succinctly without replicating XAML templates/triggers. Do not introduce reflection-heavy discovery or expression compilation requirements until the selected Mac runtime/trimming mode is validated; typed delegates or generated accessors can preserve the chosen ergonomic syntax if needed.

Keep `[ObservableProperty]` public partial properties and `[RelayCommand]` conventions. `On<Property>Changed` partial methods remain ViewModel business hooks, not native view events. Test initial value, worker-thread notifications, nested replacement, two-way loops, async commands, row reuse and late callbacks after disposal.

### 7.3 Navigation, dialogs, windows and quit

Keep shared navigation intent and portable payloads. AppKit head maps them to containment, sheet, popover or window presentation according to D6. Match existing back behavior, settings nesting, selection, composer ownership, and pop-out flows according to D2. No per-platform ViewModel rewrite to simplify a route.

A window-scoped presenter serializes conflicting dialogs and tracks cancellation, owner closure and pending operations. Account initialization must not hold a gate while requesting OAuth UI through that same gate. Use the full production `IMailDialogService`/related contracts; the PoC's one-method dialog is not sufficient. Native picker cancellation must not be reported complete while its panel is still active.

The head owns `NSApplicationDelegate` and window delegates. Native delayed termination can await a save and then explicitly reply; use native AppKit lifecycle instead of Uno proxying. Apple documents the required later reply for [delayed termination](https://developer.apple.com/documentation/appkit/nsapplication/reply(toapplicationshouldterminate:)). Validate the native run-loop behavior with WebKit and active sheets.

Quit transaction: prevent conflicting edits/presentations -> collect dirty editors -> freeze editing -> await confirmed local draft saves and calendar decisions -> stop shared runtime -> dispose presentations -> permit termination. A failed save or Cancel vetoes quit and restores still-owned editors. Do not thaw an already-disposed native session after a terminal cleanup error. Repeated quit/close requests are idempotent. OS force termination cannot promise a last-minute save; normal autosave/recovery must protect that case.

Apply D5 to window close separately from process quit. Test Dock reopen, Command+Q, menu Quit, an open native picker, an active OAuth flow, failed draft save and unsaved calendar data. Do not assert multiwindow support until dispatcher, ViewModel lifetime and draft ownership are verified across simultaneous presentations.

### 7.4 Reader/editor and security policy

Implement native WKWebView adapters against Editor.Core sessions. Share sanitizer/editor assets, MIME preparation, readiness messages and typed JSON; retain Windows WebView2 controls. Correlate asynchronous replies to the current document/session; ignore late replies after navigation/disposal. Detach script handlers, delegates and callbacks on release.

Prepare a fresh document with the correct per-message remote policy before rendering. Audit CID/content-location/data/blob URLs, redirects, CSS imports/fonts/images, malformed HTML, remote toggles, external navigation, downloads and JS message origins. The earlier policy choice permits CSP-based blocking without a generic request interceptor; it does not permit claiming blocking without native fixtures. If fixtures require another mechanism, bring that design decision back rather than weakening the policy. WebKit also has native [content rule lists](https://developer.apple.com/documentation/webkit/wkcontentruleliststore), whose suitability needs an implementation spike.

Port compose formatting, signatures, templates, images, attachments, focus/IME, undo/redo, spellcheck, Command shortcuts, find, themes and accessibility. Freeze/get-body/save ordering must use the same ready session the user edited. Test rapid message switching, close during load, repeated compose sessions and native process failures. Keep reader/editor policies separately configurable; do not give received HTML editor privileges.

### 7.5 OS services, persistence and distribution

Preserve donor Mac credential semantics, but prefer supported .NET Security/Foundation/AppKit bindings over bespoke C/Objective-C wrappers when they cover the requirement. Review retain/release, native exception boundaries, cancellation, Keychain ACL behavior and service naming. Do not carry the Uno-native build target into the new graph by default.

Maintain separate credential storage and data-protection contracts. Keychain-backed authenticated encryption must fail explicitly for denied/missing/corrupt keys. A missing key for an existing data root must never trigger silent replacement. Credential updates must not delete the last usable revision before successful database persistence. Define cleanup/recovery for orphaned revisions, failed writes, account deletion and imported backups; donor cleanup is incomplete. Preserve current sync/export boundaries so Mac secret references never become portable credentials accidentally.

Use explicit application-support/cache/temp roots and atomic nonsecret preferences. D3 determines sandbox-compatible access and user-selected file persistence. Keep attachment quarantine applied and verified before launch; clipboard/file/URL launch returns real results. Represent Cmd and Ctrl separately without rewriting existing Windows shortcut values. Menus and editor gestures must not double-execute shared commands.

Preserve shared Wino Account entitlement checks; suppress only Microsoft Store-specific presentation on Mac. Notifications, S/MIME, printing, updates/startup, activation and background behavior receive real native designs when D2/D3 are answered. Do not encode a permanent single-window or unavailable-feature assumption into a shared contract.

## 8. Ordered implementation packages

This is dependency sequencing, not authorization to launch parallel agents. Each package must leave an integrated buildable state. Root project configuration and composition changes need one integration owner if work is later delegated.

| Package | Concrete work | Depends on | Exit evidence |
| --- | --- | --- | --- |
| P0 Decisions/baseline | Resolve section 3, capture destination/donor refs, bidirectional import manifest, baseline Debug build and directly relevant tests; inventory every current Windows page/control/route. | User decisions for dependent design | Decision ledger, exact selected changes, baseline logs and known failures. |
| P1 Build graph and ViewModel consolidation | Create platform/editor-core projects; merge Core/Calendar ViewModels into Mail.ViewModels and reserve its Shell folder for donor code; retire obsolete ViewModel projects, scope RIDs/TFMs, update consumers and create separate Mac solution. Keep new Mac workload out of Windows restore. | P0 | Both ViewModel targets build, affected VM tests, Windows Debug/Release-AOT and pure shared builds; evaluated Windows/Mac graphs; no Uno references or obsolete active project edges. |
| P2 Protection/auth | Import contracts and WinUI-oriented Windows adapters, credential/recovery policy and auth callers; reconcile console/lab tooling without imposing portable targeting on Platform.Windows. | P1 | Compatibility/auth/storage tests and affected projects build; unchanged Windows formats; tools retain their existing functionality without requiring WinUI app initialization. |
| P3 Preferences/runtime/shell | Move preference ownership/generator and resource resolver, import shared runtime and donor shell source into the unified Mail.ViewModels library, and reconcile dispatcher lifetime changes. Do not import the donor Shell csproj. | P1/P2 contract stabilization | Runtime, preferences, shell, recovery and account tests; no duplicate registrations. |
| P4 Controls/editor separation | Import editor contracts/queue/serialization and Windows adapters; rename native libraries/playground; move canonical assets once; repair resource/icon/script paths. | P1 | Core/editor boundary/control tests, Windows head and playground builds, identical asset behavior. |
| P5 Windows integration gate | Reconcile capability/native/print/certificate paths and all affected VM changes; audit remaining Windows APIs in portable execution. | P2–P4 | Required Debug x64 build; direct regression tests; Release/AOT/package checks where affected; manual checklist recorded. |
| P6 AppKit foundation | Real `Wino.Mail.MacOS`, composition, AppKit helper library, controller/view conventions, typed routes, dialogs, owned lifecycle, safe quit; test real About/settings and collection/editor scenarios in the production head. No AppKit playground. | P5 automated gate; explicit manual gate status | Native Mac build and focused tests; manual foundation checks in `Wino.Mail.MacOS`; unresolved manual Windows acceptance remains visible. |
| P7 Mac services/editor | Adapt Keychain/auth/paths/attachments and implement WKWebView sessions; validate CPU native assets and minimum OS. | P6 | Native service tests, credential/OAuth/remote-content/manual editor evidence. |
| P8 Full presentation | Port every inventoried screen/control/interaction using existing VMs and reusable AppKit controls; implement required native integrations. | P6/P7 and D2/D6 | Feature-by-feature parity matrix, builds/tests and manual lab scenarios. |
| P9 Packaging/acceptance | Implement agreed distribution/signing/update boundaries; validate both architectures and clean install/update/data retention. | D3/P7/P8 | Signed bundle/native dependency checks, manual full workflow/Windows regression acceptance; no unapproved exclusions. |
| P10 PoC retirement | Remove PoC executable and nested sample VM project, PoC tasks/launch configs and obsolete references. Replace with working production build/debug tasks. | P6 replacement demonstrated | No active PoC build references, JSON/task path checks, production startup breakpoint and sample workflows manually verified. |

D1 selects foundation-first: the implementation deliverable includes P0–P6 and P10, plus the part of P7 selected by D7. P8/P9 and remaining P7 work are explicitly the next roadmap. The three D2 deferrals must have their own subsequent implementation packages; they are not permanently dropped. The foundation must not claim the full Mac port complete. Do not quietly move a foundation requirement to the roadmap when implementation encounters difficulty.

Keep renames mechanically isolated from behavioral changes for review. Import source and its corresponding test changes in bounded batches. After passing a check, repeat only when changed inputs or new evidence justify it.

## 9. Parity inventory and acceptance

Build the inventory from the active WinUI head and reusable controls, including dialogs, flyouts, secondary windows and menus—not only files named Page. Each row records Windows source/type, Mac view/controller or native equivalent, shared VM, bindings/commands, reusable control dependencies, initialization/disposal semantics, native services, tests, manual scenario, status and explicitly approved exception.

Minimum coverage groups:

| Group | Required coverage |
| --- | --- |
| Shell/navigation | Accounts/folders/modes, selection, unread badges, sidebar state, search, keyboard/focus, settings/back, restored window/layout state. |
| Mail | Large grouped/threaded lists, paging/filter/sort, row actions/context menus/swipe, drag/drop, selection persistence, reader/remote content, attachments, reply/forward, compose/autosave/send, drafts, pop-outs per D2. |
| Account/auth | Provider setup and recovery, OAuth/cancellation/refresh, account removal, linked inboxes, aliases, signatures, folder mapping and synchronization preferences. |
| Calendar | Ranges, date/time/timezone/DST, recurrence, attendee/overlap, search, edit and existing drag/move behavior, Save/Discard close rules, import per D2. Do not invent missing Windows functionality as parity. |
| Contacts/tasks | Search/groups/photos, create/edit/delete, bulk/context actions, task suggestions/completion and provider capability states. |
| Settings/content | Every settings page, themes/wallpapers/colors, fonts, keyboard shortcuts, language, storage/backup, templates, diagnostics/log export, About/What's New. |
| Wino Account/intelligence | Sign-in/out, entitlement, consent, mailbox selection, management/coverage/results, reader/composer AI and safe stale-session behavior. |
| Platform integrations | Notification actions, print/PDF, S/MIME, file/protocol activation, startup, background/tray/companion equivalents, global hotkeys and windows as resolved in D2. |
| Interaction quality | Light/dark/contrast adaptations, text sizing, localization/RTL, accessibility and screen reader, focus, Cmd/Ctrl, menus, IME/spellcheck, scaling, empty/loading/disabled/error states. |

Port order can be incremental, but acceptance cannot be based on donor's enabled-only inventory. Define representative mailbox sizes and measure startup, memory, list scrolling/selection and reader/compose latency on the M1 before broad rollout. Set measured acceptance thresholds with the user rather than inventing performance promises. Repeated open/close/rebind scenarios must show no unbounded handler/controller retention.

## 10. Build, tests and manual evidence

Required Windows command after import and final integration:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Format changed XAML before building, then check before handoff:

```powershell
pwsh -NoProfile -File scripts/maintenance/format-xaml.ps1 -Changed
pwsh -NoProfile -File scripts/maintenance/format-xaml.ps1 -Changed -Check
```

Select tests from actual imported behavior. Known donor suites include `EditorSessionBoundaryTests`, `DavCredentialStoreTests`, `IntelligenceResultKeyStoreTests`, `GmailTokenStoreTests`, `OutlookAuthenticationHostTests`, `AccountCredentialPersistenceTests`, `AccountRecoveryMetadataTests`, `PreferencesPortabilityTests`, `ApplicationRuntimeTests`, `WinoAppShellLifecycleTests`, `OptionalPlatformCapabilityTests`, `SmimeCapabilityTests`, `KeyboardShortcutPlatformDefaultsTests`, `CalendarAccountSettingsPersistenceTests`, `MessageListPreviewLifecycleTests` and `WinoIntelligenceSessionSafetyTests`. Controls projection/collection, draft synchronization and attachment/ML tests cover adjacent imports. Reconcile constructors/fixtures without weakening behavior assertions.

Example filtered command after the relevant files exist:

```powershell
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ApplicationRuntimeTests|FullyQualifiedName~PreferencesPortabilityTests|FullyQualifiedName~EditorSessionBoundaryTests"
```

Build all affected hosts, including the intelligence console, lab generator and renamed playground. Verify `net10.0` shared targets without Windows UI dependencies. Add focused dependency checks prohibiting reverse references and Uno/native UI references in shared assemblies; allow legitimate `ICommand` and conditional WinRT exposure.

Root RID/TFM, asset embedding and assembly changes touch Release/AOT/package behavior, so P1/P4/P5 also require the repository's Release build without launch and affected release-script checks. Preserve the checked-in release process; historical custom native commands are not new development wrappers. Windows build/restore must not require an Apple workload. Mac graph evaluation/build must not select Windows RIDs or restore Windows UI projects accidentally. Explicit target selection must work on both hosts; OS-name-only conditions are insufficient.

The native builds were verified on `mac.local`. Use the explicit runtime selector because the macOS SDK clears `RuntimeIdentifier` on portable project-reference builds:

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-x64 -p:WinoTargetRuntimeIdentifier=osx-x64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

Create `WinoMail.MacOS.slnx` with only the native Mac head, portable shared libraries and Mac controls/services. Keep `WinoMail.slnx` as the Windows entry point. Pin a compatible workload/Xcode combination after checking [dotnet/macios](https://github.com/dotnet/macios); distinguish build-host requirements from the app deployment minimum. Update the existing SDK configuration only with explicit compatibility evidence.

VS Code production tasks should build before debug launch, reference the verified RID-specific output and run on the Mac workspace. Do not launch one instance in a prelaunch task and debug a second inadvertently. Preserve Windows F5 and the single approved `start-wino.ps1` helper. The user explicitly requested PoC removal on 2026-10-07, overriding the earlier retirement gate. Production debugger evidence and remaining dialog/navigation acceptance are recorded separately in foundation validation.

Manual Windows lab checks: existing credential reads/login/refresh, preferences/database/MIME retention, startup/sync/account removal, mail/calendar/contacts/tasks navigation, reader remote policy, typing/drafts/attachments, printing/PDF/S/MIME, notifications/activation, tray/reopen/quit and Store behavior. Mac manual checks add Keychain denial/loss/restart, file quarantine/pickers, native menus/shortcuts, editor WebKit policies/IME, safe quit/reopen, native integration and minimum-OS/architecture acceptance. M1 execution under Rosetta is not evidence of physical Intel performance or all Intel-specific behavior.

Use current [development commands](harness/development.md) and [local lab guidance](../tools/local-lab/README.md). No agent-driven UI scripts. Deployment, when requested in implementation, must preserve package data and verify Windows identity/publisher/development status first. A build is compile evidence only; manual checks remain pending until performed.

## 11. Handoff and completion rules

Every implementation package records selected source changes, public contracts, lifetime/threading/error behavior, Windows compatibility implications, tests/builds actually executed and remaining manual checks. Never inherit donor acceptance results as new evidence.

The foundation milestone is complete only with real Windows wiring and a compiling native AppKit foundation under the approved project layout, plus recorded verification limits. The full migration is complete only when every parity row is accepted or has an explicit user-approved exception. Remove experimental projects, obsolete build tasks and unused Uno/native shims; retain the historical discussion and evidence as documentation.

The original planning review used source/project inspection and chat retrieval only. Implementation verification and remaining acceptance checks are recorded in [the import manifest](harness/platform-import-manifest.md) and [foundation validation](harness/appkit-foundation-validation.md). Decisions D7–D9 are confirmed in the ledger above.
