# Native AOT Readiness Report — Wino.Mail.WinUI + RPC layer

Scope: (A) feasibility and effort of compiling the main UI app with Native AOT now that the
companion owns sync/DB/auth-refresh, and (B) making the RPC pipe fully AOT-compatible and
type-safe. XAML `{Binding}` work is explicitly out of scope (handled separately).

## Verdict up front

- **Native AOT for the UI app is possible**, but not by flipping `PublishAot` on the current
  project. The blocker is not WinUI — it is the *dependency tree*: the UI still transitively
  references the entire sync engine (`Wino.Core` → Microsoft.Graph, Google.Apis, MailKit,
  MSAL + Broker) through `Wino.Mail.ViewModels`/`Wino.Core.ViewModels`, plus S/MIME
  cryptography running in-process. All of these are removable because their only remaining
  UI-side consumers are (1) interactive OAuth and (2) S/MIME sign/encrypt/verify — both of
  which can move to the companion.
- **The RPC layer can be made fully source-generated** with one structural trick: the
  generated request/response records live in an already-compiled assembly
  (`Wino.Ipc.Contracts`), so a *downstream* project can host a real
  `JsonSerializerContext` over them — the STJ source generator sees referenced-assembly
  types just fine; the "generators can't chain" limitation only applies within a single
  compilation.

Rough effort: **~2–3 weeks** for the dependency cuts + RPC serialization (Part A phases 0–2
and Part B), excluding the XAML binding pass.

---

## Part A — Main app Native AOT

### A1. Platform baseline

WinUI 3 supports Native AOT (WinAppSDK 1.6+, CsWinRT 2.1+ AOT-safe projections). The repo
is on WinAppSDK 2.1.4-experimental8 / .NET 10, so the platform itself is not the problem.
Requirements that will apply to `Wino.Mail.WinUI`:

- `PublishAot=true` + `WindowsAppSDKSelfContained` interplay: AOT output is inherently
  self-contained; the earlier framework-dependent WinAppSDK decision must be revisited
  (AOT app + WinAppSDK framework package is a supported combination — the manifest keeps
  the `Microsoft.WindowsAppRuntime` `PackageDependency`, only the .NET runtime is gone).
- All XAML types `partial` (CsWinRT AOT requirement), `x:Bind` over `{Binding}` (yours).
- CommunityToolkit.Mvvm 8.4 generators are AOT-clean ✓ (already in use).
- `Microsoft.Extensions.DependencyInjection` is AOT-supported (no `ValidateOnBuild`
  IL-emit path; constructor resolution is reflection but trim-rooted via registration).

### A2. The actual dependency tree today

```
Wino.Mail.WinUI
├── Wino.Mail.ViewModels ──┬── Wino.Core            ← Graph SDK, Google.Apis.*, MailKit,
├── Wino.Calendar.ViewModels┘                          MSAL + Broker + MsalCacheHelper,
│                                                      HtmlAgilityPack, NodaTime
├── Wino.Core.ViewModels ───── Wino.Core (again)
├── Wino.Services           ← Ical.Net (CalDavClient), HtmlAgilityPack, Serilog sinks
├── Wino.Core.Domain        ← MimeKit, MailKit, sqlite-net-pcl (attribute usage only)
└── direct: MSAL, sqlite-net-pcl, MimeKit-consumers, Lottie, Behaviors, WinUIEx, Sentry…
```

The single highest-leverage change: **make the ViewModels projects stop referencing
`Wino.Core`** (and `Wino.Authentication` indirectly). After the companion split, only three
things still need it UI-side — interactive auth (`InteractiveAuthHelper` →
`AuthenticationProvider`), `Wino.Core.Requests.*` construction, and a couple of
`Wino.Core.Extensions/Misc` helpers (trivially movable to Domain).

### A3. Package verdicts (UI process)

| Package | Verdict | Notes |
|---|---|---|
| Microsoft.Graph | **Remove** (transitive via Wino.Core) | Zero direct UI usage. Kiota serializers are reflection-heavy; not AOT-viable. Falls off with the Wino.Core cut. |
| Google.Apis.* | **Remove** (transitive) | Newtonsoft-based, not AOT-safe. Only UI-side use is interactive Gmail auth → move to companion (A4.1). |
| Microsoft.Identity.Client (+Broker, +MsalCacheHelper) | **Remove from UI** | Only used for interactive WAM auth. MSAL uses reflective JSON internally; not AOT-clean. Move to companion (A4.1). Also delete the stale direct refs in `Wino.Mail.WinUI.csproj`, `Wino.Mail.ViewModels.csproj`, `Wino.Core.ViewModels.csproj`. |
| MailKit | **Remove from Domain/UI** | Domain only uses `MailKit.UniqueId` (excluded `IMailService` member, `ImapMessageCreationPackage`) and `MailRenderingPageViewModel` imports it incidentally. Replace `UniqueId` with `uint`/`long` in Domain models; MailKit stays companion-only. |
| MimeKit (core parsing) | **Keep initially, then optional removal** | UI parses `.eml` for rendering/compose (`MimeFileService`, `HtmlPreviewVisitor`). The core parser is hand-written (no reflection serialization) and MimeKit ships trim-annotated; expect it to pass AOT with a handful of trimmer warnings to verify. Long-term option in A4.2 removes it entirely. |
| MimeKit.Cryptography / BouncyCastle / `WindowsSecureMimeContext` | **Move to companion** | `App.xaml.cs:117` registers the crypto context; Compose signs/encrypts (`ApplicationPkcs7Mime.Sign/Encrypt`), Rendering verifies/decrypts. BouncyCastle is the single largest AOT/trim liability in the UI. See A4.3. |
| sqlite-net-pcl | **Remove direct ref; neutralize transitive** | UI never opens SQLite anymore. Delete from `Wino.Mail.WinUI.csproj`. It remains transitively via Domain (entity `[Table]`/`[PrimaryKey]` attributes — inert at runtime). Add `<PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" ExcludeAssets="all"/>` in the UI to keep the native sqlite blob out of the package. The managed sqlite-net assembly trims to attribute metadata only. |
| Ical.Net | **Falls off** with Wino.Services split (A4.4) — only `CalDavClient` uses it; CalDav validation becomes an RPC. |
| HtmlAgilityPack / HtmlKit | Mostly companion-side; UI usage (if any in render pipeline) is parsing-only and AOT-tolerable. Verify with trim analyzer. |
| Sentry.Serilog / Serilog | **Keep** — Sentry supports Native AOT (4.x+); Serilog is AOT-fine. |
| Microsoft.Graphics.Win2D | **Keep** — WinUI drawing and bitmap normalization path; AOT-compatible replacement for the former Skia usage. |
| CommunityToolkit.WinUI 8.2 controls | **Keep** — 8.2 line is AOT-annotated. |
| CommunityToolkit.WinUI.Lottie | **Risk — verify** | Lottie-Windows JSON parsing of `.json` compositions is reflection-light but the package predates AOT annotations. If it fails: pre-compile animations to codegen classes (LottieGen) — that path is AOT-perfect, or drop to static imagery. |
| CommunityToolkit.Labs MarkdownTextBlock | **Risk — verify** (Labs packages carry no AOT guarantees). Fallback: render markdown to HTML in companion / use WebView2. |
| Microsoft.Xaml.Behaviors.WinUI.Managed 3.0 | **Verify** — 3.x added trimming support; behaviors instantiate via `x:Type`-ish reflection. Audit usages; replace trivial ones with code-behind event wiring. |
| WinUIEx 2.9 | **Keep** — AOT-supported since 2.5. |
| System.Reactive (ViewModels) | **Verify** — Rx core is AOT-workable, but check usage; if it is only a couple of throttle/debounce pipelines, replacing with handwritten timers removes a sizable assembly. |
| Wino.Mail.Contracts (ApiEnvelope DTOs) | **Keep** — plain DTOs; include them in the source-generated STJ context (Part B). |
| Microsoft.Windows.SDK.BuildTools.MSIX | **Remove** — leftover from single-project MSIX. |

### A4. Code-level moves

#### A4.1 Interactive OAuth → companion (kills MSAL + Google.Apis in UI)

Key insight: WAM only needs *an* HWND to parent its broker dialog — and the broker runs in
its own process anyway, so a cross-process HWND is fine. The Google flow launches a browser
plus a loopback listener and needs no window at all.

- New RPC on the control surface (or a dedicated `IAuthorizationService`):
  `Task<TokenInformationEx> RequestInteractiveAuthorizationAsync(MailProviderType providerType, Guid? accountId, long parentWindowHandle, bool proposeCopyAuthUrl, bool forceInteractive)`.
- Companion implements it with the existing `AuthenticationProvider`; its
  `HeadlessNativeAppService.GetCoreWindowHwnd` becomes settable per-call (pass the UI HWND
  through to `WithParentActivityOrWindow`). Token caches are already shared.
- UI deletes `InteractiveAuthHelper`, the `IAuthenticationProvider` injections, and the
  `RegisterCoreServices()` call in `App.ConfigureServices` (audit the few remaining
  registrations it provided to the UI — most are sync-engine-only).
- Watch-outs: foreground rights (the companion-spawned broker window may need
  `AllowSetForegroundWindow`/UI activation cooperation — the UI should call the RPC while it
  is foreground, which it always is for these flows); the `google.pw.oauth2` protocol
  activation lands on the Mail app entry — forward it to the companion over the existing
  pipe (mirror of the toast-forwarding path).

#### A4.2 MIME rendering (optional, phase 2+)

Today the UI parses `.eml` with MimeKit and builds HTML via `HtmlPreviewVisitor`. To make
the UI MIME-free entirely: add `IMailRenderService` RPC — companion parses the MIME, writes
HTML + extracted inline resources/attachments to the shared cache folder, returns a
serializable `MailRenderModel` (paths + metadata, no `MimeMessage`). Compose similarly
operates on a draft-model DTO; the companion already produces drafts as base64 MIME.
This is the biggest single working-set/startup win after the auth cut, but it is not a
hard AOT prerequisite if MimeKit core verifies AOT-clean.

#### A4.3 S/MIME → companion

- Sign/encrypt: replace the `ApplicationPkcs7Mime.Sign/Encrypt` block in
  `ComposePageViewModel` with an RPC: `Task SignAndEncryptDraftAsync(Guid accountId, Guid fileId, string signingCertificateThumbprint, bool encrypt)`.
  Certificate *selection* stays in the UI: `SmimeCertificateService` only enumerates the
  user store via `System.Security.Cryptography.X509Certificates` (fully AOT-supported);
  only the thumbprint crosses the pipe. Companion loads the cert from the same CurrentUser
  store — same user, same store, no key material crosses.
- Verify/decrypt: companion returns signature validity + decrypted body as part of the
  render model (pairs naturally with A4.2). `CryptographyContext.Register` moves from
  `App.xaml.cs` to the companion `Program`.

#### A4.4 Project splits

- `Wino.Services` → split out `Wino.Services.Shared` (PreferencesService,
  ApplicationConfiguration, TranslationService, MimeFileService*, ServiceConstants) that the
  UI references; DB-backed services + CalDavClient stay in `Wino.Services`
  (companion-only). `AccountSetupProgressPageViewModel`'s direct `ICalDavClient` use becomes
  an RPC (`ISynchronizationManager.TestCalDavConnectivityAsync(CustomServerInformation)` —
  symmetric with the existing IMAP test).
- `Wino.Mail.ViewModels`/`Wino.Calendar.ViewModels`/`Wino.Core.ViewModels` drop the
  `Wino.Core` reference once A4.1 lands and the request-model issue below is fixed.

#### A4.5 Request models — also a live bug

ViewModels construct `Wino.Core.Requests.*` objects (`MailCategoryCreateRequest`,
`CreateRootFolderRequest`, …) and pass them to
`IWinoRequestDelegator.ExecuteAsync(Guid, IEnumerable<IRequestBase>)` — which is
`[WinoRpcExclude]`d, so the proxy **throws `NotSupportedException` today** (category
management and some folder operations are currently broken). Fix and AOT-cut in one move:
define serializable operation-descriptor DTOs in Domain (e.g.
`MailCategoryOperation { Kind, Category, … }`, folder ops likewise), add typed delegator
RPC methods for them, and let the companion map descriptors → `IRequestBase`. This removes
the last `Wino.Core.Requests` dependency from the ViewModels.

### A5. Phased plan & effort

| Phase | Work | Effort |
|---|---|---|
| 0 | Delete dead refs (MSAL/sqlite/MSIX-buildtools from UI csprojs); fix A4.5 descriptor DTOs (bug fix anyway) | 1–2 days |
| 1 | Interactive auth → companion (A4.1); CalDav test RPC; ViewModels drop Wino.Core; Wino.Services split | 3–5 days |
| 2 | S/MIME → companion (A4.3); MailKit out of Domain; enable `IsAotCompatible`+trim analyzers on UI tree, burn down warnings | 3–5 days |
| 3 | `PublishAot` experiment build; triage Lottie/Behaviors/Labs/Rx verdicts; fallback swaps | 2–4 days |
| 4 | XAML `{Binding}`/partial-class pass | (yours) |

Startup wins arrive before AOT: phases 0–2 alone remove Graph/Google/MSAL/BouncyCastle/
Ical.Net/sqlite-native from the package and JIT surface. If AOT stalls on a stubborn
control library, `ReadyToRun` + the slimmed tree is a strong fallback.

---

## Part B — Fully AOT-compatible, type-safe RPC

### B1. Current state

`Wino.Ipc` (framing/protocol) is already AOT-clean with its own `IpcProtocolJsonContext`.
The payload layer is not: `WinoIpcJson` uses the reflection `DefaultJsonTypeInfoResolver`
because the STJ source generator cannot see types emitted by our `RpcGenerator` *within the
same compilation* (Roslyn generators don't chain).

### B2. Proposed architecture: split the compilation

The chaining limitation disappears across assemblies — the STJ generator resolves
`typeof(X)` against *referenced metadata* without issue:

```
Wino.Core.Domain ──► Wino.Ipc.Contracts ──► Wino.Ipc.Serialization ──► UI / Companion
   (interfaces)        RpcGenerator emits      real source file:           call
                       records/proxies/        WinoIpcJsonContext :        WinoIpcJson.Initialize(
                       dispatchers (compiled)  JsonSerializerContext       WinoIpcJsonContext.Default)
                                               [JsonSerializable(...)]     at startup
                                               × every crossing type
                                               → STJ source generator
```

1. **`Wino.Ipc.Contracts` unchanged** — records, proxies, dispatchers, event registry stay
   generated there and compile into the assembly.
2. **New `Wino.Ipc.Serialization`** holds one file: a partial
   `WinoIpcJsonContext : JsonSerializerContext` with `[JsonSerializable]` entries for every
   request/response record, every method return type, and every `IUIMessage`. The STJ
   source generator computes the full object-graph closure (MailCopy → MailAccount →
   Preferences → …) and — crucially — **fails compilation on anything non-serializable**,
   which is exactly the type-safety gate requested. Options (`PropertyNameCaseInsensitive`
   etc.) move onto `[JsonSourceGenerationOptions]`.
3. **`WinoIpcJson` becomes a resolver holder**: `Initialize(IJsonTypeInfoResolver)` called
   once at startup by both processes; `GetTypeInfo<T>()` resolves from the configured
   context and throws fail-fast for unregistered types. **No changes to any generated
   proxy/dispatcher/registry code** — they already funnel through `WinoIpcJson.GetTypeInfo<T>()`.
4. `Wino.Ipc.Contracts` flips back to `IsAotCompatible=true`.

**Keeping the attribute list honest.** Two options, recommend both:
- *Authoritative:* a tiny prebuild tool (console, `MetadataLoadContext` over the compiled
  `Wino.Ipc.Contracts.dll`) regenerates `WinoIpcJsonContext.g.cs` on disk as a build step of
  `Wino.Ipc.Serialization`. Real source on disk → STJ generator consumes it. Deterministic,
  zero hand-maintenance.
- *Safety net:* a unit test that reflects over the Contracts assembly, computes the
  required type set, and diffs it against `WinoIpcJsonContext`'s registered types — fails
  with the exact missing `[JsonSerializable(typeof(...))]` lines to paste. (If you prefer no
  custom build steps, this test alone makes a hand-maintained list workable: it changes only
  when an RPC signature changes.)

### B3. Type-surface hygiene rules (enforced twice)

- Gate 1 (exists): `RpcGenerator` WINORPC001 rejects interface/abstract/foreign types in
  *signatures*.
- Gate 2 (new): STJ source generation rejects them anywhere in the *object graph* at
  compile time. Known cleanups it will force, found by inspection:
  - interface-typed navigation properties on entities (`CalendarItem.AssignedCalendar`
    already `[JsonIgnore]`d — audit siblings like `MailCopy.AssignedFolder/AssignedAccount`
    which are concrete and fine; anything `IMailItemFolder`-typed in crossing models must
    become concrete or ignored);
  - `MailKit.UniqueId` leftovers in Domain models (A4 removes them);
  - `JsonElement` payloads in `ApiEnvelope<T>` are supported by source-gen ✓;
  - any future polymorphism crossing the pipe must use `[JsonDerivedType]` explicitly.
- The event registry and the request envelope writer already avoid `object`-typed
  serialization (typed switch + `Utf8JsonWriter`), so they inherit AOT-safety from the
  context switch automatically.

### B4. Effort

| Work | Effort |
|---|---|
| `Wino.Ipc.Serialization` project + context + `WinoIpcJson` resolver swap | ~1 day |
| Prebuild list generator (or completeness test) | ~1 day |
| Object-graph cleanup forced by gate 2 (JsonIgnore/concrete-type fixes across Domain) | 1–2 days |
| Re-enable `IsAotCompatible` on Contracts, AOT publish smoke of a console host using proxies | ½ day |

---

## Suggested order of attack

1. Part B first (small, self-contained, makes every later DTO change compile-checked).
2. A4.5 descriptor DTOs (fixes the live `NotSupportedException` bug).
3. A4.1 auth move → delete MSAL/Google from the UI tree, drop Wino.Core from ViewModels.
4. A4.3 S/MIME move, A4.4 splits, package deletions.
5. Trim-analyzer burn-down → `PublishAot` experiment → control-library triage.
