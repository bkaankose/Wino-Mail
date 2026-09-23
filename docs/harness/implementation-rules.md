# Wino implementation rules

Read the relevant section before changing C#, XAML, translations, storage, or editor assets.
Build and runtime commands are in [development commands](development.md).

## Architecture

```text
src/Wino.Core.Domain       Contracts, entities, translations, enums
src/Wino.Core              Synchronization, authentication, request processing
src/Wino.Services          Database, mail, folder, account, preference services
src/Wino.Core.ViewModels   Shared ViewModels
src/Wino.Mail.ViewModels   Mail ViewModels
src/Wino.Messaging         Messenger contracts
src/Wino.Mail.WinUI        Active WinUI 3 application
controls                   Reusable controls, editor, and playground
tests                      Automated tests
```

Mail requests flow through `WinoRequestDelegator`, `WinoRequestProcessor`, provider synchronizers, change processors, the local SQLite database, and messenger events. Initial synchronization queues identifiers and downloads MIME content on demand.

Register core services in `CoreContainerSetup`, shared services in `ServicesContainerSetup`, and ViewModels in `App.xaml.cs`.

Published cross-repository dependencies must use unconditional `PackageReference` items. A sibling checkout must never change the dependency graph.

If a change requires a new package, run the security audit and publish the new NuGet version during the task. Then update the centrally managed package version in every consumer. Do not substitute a local NuGet feed or sibling-project reference.

## Core implementation rules

- Give collection expressions an explicit concrete backing type when the target is a non-mutable interface such as `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, or `IEnumerable<T>`. Never assign an uncast collection expression directly to these interfaces: the compiler-generated collection type can fail WinRT trimming and Native AOT checks with `CsWinRT1032`. Preserve the interface API with an explicit array cast, for example `public IReadOnlyList<Option> Options { get; } = (Option[])[new(...), new(...)];`, or construct an array or `List<T>` explicitly. Apply this rule to properties, fields, return values, and arguments. When changing these expressions in WinRT-facing code, run `./scripts/wino.ps1 build app -Configuration Release`. A successful Debug build does not prove AOT compatibility.
- Use public partial properties with `[ObservableProperty]`.
- Do not annotate private backing fields.
- Register messenger handlers in `RegisterRecipients()` and unregister them in `UnregisterRecipients()`.
- Messenger recipients run on background threads by default. Marshal UI-bound state, collections, navigation, windows, JumpLists, and other WinRT APIs through `ExecuteUIThread(...)` or the appropriate dispatcher.
- Treat code after `ConfigureAwait(false)` as background-thread code until explicitly dispatched.
- Keep ViewModels limited to UI state and interaction. Put authentication, account API, token, preferences, and other business operations in services.
- Avoid new NuGet packages when existing platform or repository libraries are sufficient.
- Use `IWinoLogger` for errors and wrap async external operations in `try`/`catch`.
- Use logical vertical spacing (code paragraphing) in C#: separate distinct guard clauses, retrievals, transformations, UI updates, and returns with a blank line; keep expressions that form one operation together. Do not add blank lines merely to isolate every individual statement.

## WinUI and XAML rules

- Before designing a new user-facing Wino feature or changing a visual pattern, read `docs/wino-design-guideline.md`. Apply its Wino-specific layout, surfaces, command, state, accessibility, and verification decisions; update the guide when establishing a reusable new pattern.

- Do not add XAML-backed controls, flyouts, templates, or visual composition in `.xaml.cs`. Keep code-behind for handlers and view glue.
- `DataTemplate` and `ControlTemplate` do not support visual states in this project. Never put visual states inside these templates.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not constructors.
- Give every element using `x:Load` an `x:Name`.
- Do not introduce `IValueConverter` classes. Use direct WinUI conversion or existing `XamlHelpers` methods.
- `x:Bind` does not convert `double` to `GridLength`. Use an existing helper.
- Use typed `ItemTemplate` bindings and explicit `SelectedItem` for `ComboBox`.
- Do not use `DisplayMemberPath` or `SelectedValuePath`.
- Keep every shell navigation `DataTemplate` in `Styles/ShellMenu/ShellMenuTemplates.xaml` and its existing code-behind. Expose templates as public selector properties and wire them with `{StaticResource}`; do not resolve XAML resources through `Application.Current.Resources` or mutate its merged dictionaries at runtime.
- Prefer `[GeneratedDependencyProperty]` over manual dependency-property registration.
- Use command `CanExecute` and `[NotifyCanExecuteChangedFor]` instead of binding a command button's `IsEnabled` when possible.
- Use `{ThemeResource}` for visual resources and preserve Light, Dark, High Contrast, keyboard, pointer, touch, and automation behavior.
- Follow `controls/AGENTS.md` for reusable control templates, parts, playground samples, automation peers, and lifecycle rules.

### Icons

Every icon comes from the WinoIcons fonts. Their source is `icons/manifest.json` plus `icons/svg`; see [icons/README.md](../../icons/README.md).

- In app XAML, use `<coreControls:WinoFontIcon Icon="Name" />`. Use `WinoFontIconSource` where an `IconSource` is expected.
- Never add `SymbolIcon`, `PathIcon`, a `FontIcon` with a Segoe glyph, or `Icon="Symbol"` shorthand on `AppBarButton`, `NavigationViewItem`, `SettingsCard` and similar hosts.
- `WinoIconGlyph` is generated into `Wino.Core.Domain.Enums`. Models and ViewModels expose `WinoIconGlyph`, or a glyph string from `WinoIconGlyphs.GetGlyph(...)` when a controls-library API takes a string. Never expose Segoe codepoints.
- Inside `Wino.Core.Domain`, do not put `[ObservableProperty]` on a `WinoIconGlyph` property. The enum is generated in that assembly, so the MVVM generator cannot resolve it. Use a plain property.
- Do not set `FontSize` on a `WinoFontIcon` hosted in an `Icon` or `HeaderIcon` slot or a `Viewbox`. The host sizes it.
- The icon style (monochrome or colorful) is applied by `NewThemeService`. It rewrites `WinoIconFontFamily` in the theme dictionaries of `Styles/WinoIcons.xaml`, the same way it applies accent colors. Do not set `FontFamily` on an individual `WinoFontIcon`.
- To add an icon, run `python icons/tools/add_fluent_icon.py <fluent_name> <Name> [--accent <palette key>]`, then `python icons/tools/build_fonts.py`. Commit the manifest, the SVGs and every regenerated font. Only use `--accent` where color carries meaning. Toolbar and menu commands stay monochrome.
- Check with `.\scripts\audit-xaml-icons.ps1` and `python icons/tools/build_fonts.py --check`.

Format changed XAML with the repository-pinned XAML Styler before the build. Passive mode must pass before handoff:

```powershell
.\scripts\wino.ps1 xaml changed
.\scripts\wino.ps1 xaml changed -Check
```

The editor extension is a convenience. The pinned command-line result is the formatting source of truth.

## Localization, storage, and rendering

- Add English source strings only to `src/Wino.Core.Domain/Translations/en_US/resources.json`.
- Use generated `Translator` properties. XAML translation bindings use `Mode=OneTime` because `Translator` does not implement `INotifyPropertyChanged`.
- Treat non-English resource files as externally managed and do not edit them.
- SQLite data lives in the publisher cache. EML files live in app local storage and are resolved through `MimeFileService.GetMimeMessagePath()`.
- `controls/Wino.Editor` is the only reader/editor HTML, CSS, and JavaScript asset source. Preserve its document-ready bridge and on-demand MIME loading.


