# Mail-Cal implementation rules

Read the relevant section before changing C#, XAML, translations, storage, or editor assets.
The architecture, core, localization, and storage rules apply to both applications. The WinUI and XAML rules apply to the Windows application only.
Build and runtime commands are in the [Windows development guide](development-windows.md).

## Architecture

```text
src/MailCal.Core.Domain       Contracts, entities, translations, enums
src/MailCal.Core              Synchronization, authentication, request processing
src/MailCal.Services          Database, mail, folder, account, preference services
src/MailCal.Mail.ViewModels   Active Mail, Calendar, Contacts, To Do, Settings, and Shell ViewModels
src/MailCal.Messages          Messenger contracts
src/MailCal.NotificationHost  Toast activation host for the Windows package
src/MailCal.SourceGenerators  Source generators and the MCAOT001 analyzer
src/MailCal.Platform.Windows  Windows platform services
src/MailCal.Mail.WinUI        Windows application (WinUI 3)
controls                      Shared control contracts, editor engine, WinUI controls, playground
```

Mail requests flow through `MailCalRequestDelegator`, `MailCalRequestProcessor`, provider synchronizers, change processors, the local SQLite database, and messenger events. Initial synchronization queues identifiers and downloads MIME content on demand.

Register core services in `CoreContainerSetup` (`RegisterCoreServices`) and shared services in `ServicesContainerSetup` (`RegisterSharedServices`). Each application registers its platform services and ViewModels in its own composition root: `App.xaml.cs` on Windows.

Dependencies use unconditional `PackageReference` items with versions in `Directory.Packages.props`. A sibling checkout must never change the dependency graph. Do not substitute a local NuGet feed or sibling-project reference. See [Dependencies](../../AGENTS.md#dependencies).

## Core implementation rules

- Give collection expressions an explicit concrete backing type when the target is a non-mutable interface such as `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, or `IEnumerable<T>`. Never assign an uncast collection expression directly to these interfaces: the compiler-generated collection type can fail WinRT trimming and Native AOT checks with `CsWinRT1032`. Preserve the interface API with an explicit array cast, for example `public IReadOnlyList<Option> Options { get; } = (Option[])[new(...), new(...)];`, or construct an array or `List<T>` explicitly. Apply this rule to properties, fields, return values, and arguments. A collection that reaches an items source (`ItemsSource`, `MenuItemsSource`, `CollectionViewSource.Source`) is the exception: expose its concrete type (an array, `List<T>`, `ObservableCollection<T>`, or a `[GeneratedWinRTExposedType]` collection), because an interface static type hides the runtime collection from CsWinRT and the list control rejects it in Release. The `MCAOT001` analyzer enforces this. When changing these expressions in WinRT-facing code, run `dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64`. A successful Debug build does not prove AOT compatibility.
- Use public partial properties with `[ObservableProperty]`.
- Do not annotate private backing fields.
- Register messenger handlers in `RegisterRecipients()` and unregister them in `UnregisterRecipients()`.
- Messenger recipients run on background threads by default. Marshal UI-bound state, collections, navigation, windows, and platform UI APIs (WinRT) through `ExecuteUIThread(...)` or the appropriate dispatcher.
- Treat code after `ConfigureAwait(false)` as background-thread code until explicitly dispatched.
- Keep ViewModels limited to UI state and interaction. Put authentication, account API, token, preferences, and other business operations in services.
- Avoid new NuGet packages when existing platform or repository libraries are sufficient.
- Use `IMailCalLogger` for errors and wrap async external operations in `try`/`catch`.
- Use logical vertical spacing (code paragraphing) in C#: separate distinct guard clauses, retrievals, transformations, UI updates, and returns with a blank line; keep expressions that form one operation together. Do not add blank lines merely to isolate every individual statement.

## WinUI and XAML rules (Windows)

- Before designing a new user-facing Mail-Cal feature or changing a visual pattern, read `docs/mailcal-design-guideline.md`. Apply its Mail-Cal-specific layout, surfaces, command, state, accessibility, and verification decisions; update the guide when establishing a reusable new pattern.

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

Every icon comes from the MailCalIcons fonts. Their source is `icons/manifest.json` plus `icons/svg`; see [icons/README.md](../../icons/README.md).

- In app XAML, use `<coreControls:MailCalFontIcon Icon="Name" />`. Use `MailCalFontIconSource` where an `IconSource` is expected.
- Never add `SymbolIcon`, `PathIcon`, a `FontIcon` with a Segoe glyph, or `Icon="Symbol"` shorthand on `AppBarButton`, `NavigationViewItem`, `SettingsCard` and similar hosts.
- `MailCalIconGlyph` is generated into `MailCal.Core.Domain.Enums`. Models and ViewModels expose `MailCalIconGlyph`, or a glyph string from `MailCalIconGlyphs.GetGlyph(...)` when a controls-library API takes a string. Never expose Segoe codepoints.
- Inside `MailCal.Core.Domain`, do not put `[ObservableProperty]` on a `MailCalIconGlyph` property. The enum is generated in that assembly, so the MVVM generator cannot resolve it. Use a plain property.
- Do not set `FontSize` on a `MailCalFontIcon` hosted in an `Icon` or `HeaderIcon` slot or a `Viewbox`. The host sizes it.
- The icon style (monochrome or colorful) is applied by `NewThemeService`. It rewrites `MailCalIconFontFamily` in the theme dictionaries of `Styles/MailCalIcons.xaml`, the same way it applies accent colors. Do not set `FontFamily` on an individual `MailCalFontIcon`.
- To add an icon, follow [Add an icon](../../icons/README.md#add-an-icon). From an SVG file, run `python icons/tools/add_svg_icon.py <Name> --svg <file>`. From Fluent UI System Icons, run `python icons/tools/add_fluent_icon.py <fluent_name> <Name> [--accent <palette key>]`. Then run `python icons/tools/build_fonts.py`. Commit the manifest, the SVGs and every regenerated font. Never copy an SVG into `icons/svg` by hand; the font build ignores files that are not in the manifest. Only use `--accent` where color carries meaning. Toolbar and menu commands stay monochrome.
- Check with `.\scripts\maintenance\windows\audit-xaml-icons.ps1` and `python icons/tools/build_fonts.py --check`.
- Toast button icons cannot use the font. They are PNG files in `Assets/NotificationIcons`: 16 px at 100% scale, in five scales and five variants (`theme-dark`, `theme-light`, `contrast-black`, `contrast-white`, neutral). Create every toast button through `CreateButton` in `NotificationBuilder`, which requires an icon. Windows switches all buttons of a toast to icon style once one has an icon. To add an icon, map its name to an `icons/svg` source in `scripts/maintenance/windows/build-notification-icons.ps1` and run the script.

Format changed XAML with the repository-pinned XAML Styler before the build. Passive mode must pass before handoff:

```powershell
.\scripts\maintenance\windows\format-xaml.ps1 -Changed
.\scripts\maintenance\windows\format-xaml.ps1 -Changed -Check
```

The editor extension is a convenience. The pinned command-line result is the formatting source of truth.

## Localization, storage, and rendering

- Add English source strings only to `src/MailCal.Core.Domain/Translations/en_US/resources.json`.
- Use generated `Translator` properties. XAML translation bindings use `Mode=OneTime` because `Translator` does not implement `INotifyPropertyChanged`.
- Treat non-English resource files as externally managed and do not edit them.
- The main SQLite database and EML files live under `IApplicationConfiguration.ApplicationDataFolderPath`: the package LocalState folder on Windows. Resolve them through `MainDatabasePaths` and `MimeFileService.GetMimeMessagePath()`; never build platform paths in shared code. The Windows publisher cache is read only as a legacy migration source.
- `controls/MailCal.Editor.Core` is the only reader/editor HTML, CSS, and JavaScript asset source for WebView2 and WKWebView. Preserve its document-ready bridge and on-demand MIME loading.


