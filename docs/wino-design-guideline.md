# Wino design guideline

> **Status:** Draft baseline, 2026-08-15  
> **Use this for:** Every user-facing Wino feature, page, control, dialog, command, state, or visual change in `src/Wino.Mail.WinUI` and reusable Wino controls.

Wino is a Windows productivity workspace for correspondence. This guide turns Microsoft’s Windows and Fluent guidance into product-specific decisions, while protecting Wino’s signature: a calm, personal workspace with dense, fast mail and calendar workflows.

This is a design starting point, not a mandate to rewrite working UI. Apply the guidance proportionately to the change. Pair it with the repository’s implementation rules in `AGENTS.md`.

## 1. Wino’s north star

### Focused correspondence

Email reading and writing are central. Navigation, account identity, automation, and metadata must support the message, never compete with it.

### Dense, not crowded

Wino earns information density through alignment, hierarchy, progressive disclosure, and keyboard access. It must not use tiny type, ambiguous icons, or permanent command clutter to fit more data.

### Personal Windows workspace

Mica, Acrylic, image-backed themes, account context, and preference choices make Wino feel owned. They are ambient character, not a replacement for readable task surfaces or accessible contrast.

### Familiar but distinct

Use native WinUI controls and standard Windows patterns wherever they fit. Wino’s distinction should come from its task model, theme system, and purposeful composition—not reinvented controls for familiar actions.

## 2. Microsoft principles translated for Wino

| Windows principle | Wino decision |
| --- | --- |
| Effortless | Put frequent actions near the message, list item, event, or setting they affect. Preserve keyboard and context-menu routes. |
| Calm | Use whitespace, layered surfaces, and restrained color to create focus. Give each region one clear hierarchy and each task a clear primary action. |
| Personal | Respect the selected Wino theme, account identity, system settings, text scale, and contrast theme. |
| Familiar | Prefer native controls and Windows command vocabulary. Do not make the user learn a custom interaction for a familiar task. |
| Complete + coherent | A feature works in Light, Dark, High Contrast, keyboard, mouse, touch, localization, narrow windows, and non-happy-path states. |

## 3. Product architecture: choose the feature’s home first

Wino is a mode-based productivity app. Do not begin with a control; first decide which task area owns the feature.

| Area | Primary pattern | Design priority |
| --- | --- | --- |
| Mail | Three-pane / list-detail workspace | Message focus, scan speed, selection continuity, contextual commands. |
| Calendar | Time-grid workspace with details | Time legibility, overlap handling, keyboard creation and editing. |
| Contacts | Browse + details | Recognition, clear identity, action proximity. |
| Settings and accounts | Hierarchical settings pages | Plain-language grouping, safe changes, recoverable operations. |
| Compose and event editing | Task-focused editor | Uninterrupted input, validation at point of need, explicit save/send state. |
| Intelligence | Supporting insight layer | Provenance, user control, visible status. Never obscure a core mail task. |

Keep global modes in the existing shell. Add a top-level mode only for a persistent, distinct job. Settings pages, details, and flows belong in-mode and retain predictable Back behavior and state.

## 4. Layout and density

### Default composition

- Use a stable structural pane and let the working content flex.
- Give each content region one visible anchor: page title, list header, reader header, or calendar period.
- Use 12, 16, 24, and 32 epx as the normal spacing rhythm. Add space to express grouping, not decoration.
- Let reader, editor, and calendar content use the available window. Avoid small centered content islands in a large workspace.
- Use cards only where elevation segments a distinct task or information group. Do not turn every section into a card.

### Responsive behavior

Breakpoints use the app window’s effective pixels, not physical monitor resolution.

| Window width | Required behavior |
| --- | --- |
| Large (`>=1008` epx) | Use simultaneous list/detail or calendar/details when it improves the task. |
| Medium (`641–1007` epx) | Preserve the task hierarchy. Collapse secondary panes or use compact navigation. |
| Small (`<=640` epx) | Move from simultaneous panes to a deliberate drill-in flow. Keep every critical command reachable through an overflow, menu, or secondary surface. |

Use the least disruptive responsive change that preserves the user’s task: reposition, resize, reflow, selectively show/hide secondary metadata, then re-architect only when width changes the task model.

## 5. Foundations

### Typography

- Use the system type ramp and semantic WinUI text styles.
- Use titles to establish page hierarchy, body text for scanability, and subdued but readable metadata.
- Use sentence case and concise, task-led labels.
- Do not use placeholder text as the only field label.
- Use stable alignment for dates, times, counts, and keyboard shortcuts.

### Color

Color is semantic. Accent indicates primary action, selection, or focus. Success, warning, and danger describe outcomes, not decoration.

- Use existing semantic brushes and `{ThemeResource}` at UI usage sites.
- Do not add hard-coded colors for new features.
- Do not communicate status by color alone; pair it with text, iconography, or position.
- Keep meaningful visible text at 4.5:1 contrast or better against its real background.

### Shape and elevation

- Use 4 epx rounding for controls and in-page backplates.
- Use a fully rounded 16 epx radius for the compact 32 epx `WinoSearchBar`, matching the familiar Windows Settings search shape.
- Use 8 epx rounding for dialogs, flyouts, TeachingTips, and top-level containers.
- Do not round edges where adjacent panels intentionally meet flush.
- Let built-in controls provide their appropriate elevation. Shadows explain hierarchy; they are not decoration.

### Icons

- Use familiar Windows symbols with accessible names.
- Pair unfamiliar, consequential, destructive, and low-frequency icon actions with visible labels.
- Icon-only buttons require a tooltip and an accessible name.
- Do not use emoji as UI iconography.

## 6. Surfaces and Wino personalization

Wino already supports default, Acrylic, custom, and image-backed themes. Preserve that personal touch while ensuring task content is readable and dependable.

| Layer | Use in Wino | Rule |
| --- | --- | --- |
| Base layer | Window backdrop, app shell, navigation, ambient selected theme | May expose Mica, Acrylic, or the selected image/background. |
| Content layer | Reading pane, editor, calendar canvas, settings content | Use a calm opaque or system-aware surface that protects legibility. |
| Card / information area | Bounded settings groups, explanatory status, optional detail | Use only where it segments a real task. |
| Transient overlay | Dialog, flyout, menu, TeachingTip | Use built-in elevation and overlay geometry. |

Reuse existing Wino resources and styles where applicable:

- `WinoApplicationBackgroundColor`
- `ReadingPaneBackgroundColorBrush`
- `MailListHeaderBackgroundColor`
- `PageRootBorderStyle`
- `InformationAreaGridStyle`
- `WinoDialogStyle`
- `TransparentActionButtonStyle`

The selected backdrop is decorative. The content layer, selected state, focus visual, and readable text must remain clear independently of it.

## 7. Navigation

- Keep folder and account navigation structural and stable.
- Keep selection visibly tied to the current working context.
- Use `TabView` only for simultaneous documents or sessions, not as a generic page switcher.
- Use `SelectorBar` only for two or three peer modes.
- A deep flow needs a meaningful title and deterministic way back; do not use window close as navigation.
- Preserve state when users move between Wino modes or open detail views.

## 8. Commands, feedback, and recovery

### Command placement

| Need | Pattern |
| --- | --- |
| Frequent, object-specific action | Put it on the canvas or command bar near the affected object. |
| Secondary or rare action | Use an overflow, `MenuFlyout`, or context menu. |
| Text selection or contextual editor action | Use `CommandBarFlyout` or a context menu. |
| Major irreversible action | Use explicit text and confirmation. |

### Feedback hierarchy

- Use inline feedback for a field or localized operation.
- Use `InfoBar` for an actionable, non-blocking page status.
- Use a flyout for compact contextual feedback or settings.
- Use `ContentDialog` only for a decision that must interrupt the task.
- Prefer undo for recoverable actions such as normal mail deletion. Confirm permanent or otherwise major consequences.
- Use consequence-led labels: **Delete permanently**, not **OK**.

## 9. Core content patterns

| When you need… | Use | Wino rule |
| --- | --- | --- |
| Many messages, accounts, folders, or contacts | `ListView` and existing Wino list styles | Virtualize, retain keyboard selection, separate primary content from metadata, and never wrap the collection in a `ScrollViewer`. |
| Reader or editor | Full-bleed content with compact command chrome | Protect the reading/writing measure. Keep themed personalization behind the content layer. |
| Settings | Section header + labeled controls + concise helper text | Reuse settings header and information-area patterns. Group by user goal, not implementation subsystem. |
| A simple choice | `RadioButtons` for 2–3 choices; `ComboBox` for 4+ | Show the selected value clearly. Do not create custom segmented pills. |
| Search | `AutoSuggestBox` | Make scope and loading state clear; retain Wino semantic-search behavior. |
| Long work | Inline progress with cancel/retry where meaningful | State what is happening and what follows. A spinner alone is not enough. |

## 10. Required states

Every collection, request, and editor needs intentional normal and non-normal states.

| State | Requirement |
| --- | --- |
| Loading | Say what is loading. Keep stable existing content when possible. Skeletons must match the real layout. |
| Empty | Explain the absence and offer the next useful action: create, connect, adjust a filter, or refresh. |
| Error / offline | State the useful cause in plain language and offer recovery. Keep offline and permission failures distinct from generic errors. |
| Selection | Support pointer and keyboard selection; maintain a visible selected state and define behavior when it disappears. |
| Saving / sync | Show status near the affected content. Prevent duplicate submission without silently discarding work. |
| Privacy / intelligence | State availability, data scope, origin, and user control in text. Do not imply an AI result is authoritative action. |

## 11. Accessibility is a release requirement

Accessibility preserves Wino’s core purpose for people using keyboard navigation, screen readers, magnification, text scaling, contrast themes, touch, or voice input. It is not a final polish pass.

| Requirement | Wino standard | Verification |
| --- | --- | --- |
| Names and roles | Every interactive or informative non-text element has a concise accessible name. Icon-only controls also have a tooltip. Static content uses `TextBlock`/`RichTextBlock`, not a disabled input or artificial tab stop. | Inspect the UI Automation tree and listen with Narrator. |
| Keyboard | All actions work without a pointer. Focus order follows reading/task order; focus returns to the invoking element after a flyout or dialog closes. | Complete the task using Tab, Shift+Tab, arrows, Enter, Space, Esc, and Wino shortcuts only. |
| Focus | Focus is visible in every theme and never obscured by a themed background or selection highlight. | Navigate each surface by keyboard in Light, Dark, and a Contrast theme. |
| Color and contrast | Visible meaningful text and icons meet 4.5:1 against the effective background. Status has a non-color cue. | Measure foreground/background pairs, including personalized themes. |
| Contrast themes | Custom resources include a `HighContrast` dictionary with system resources when needed. Do not set `HighContrastAdjustment="None"` unless the complete visual has system-aware replacements. | Exercise the feature in a Windows Contrast theme. |
| Text and display scaling | Keep text scaling enabled. At enlarged text and display scale, text wraps rather than clips and commands remain reachable. | Check enlarged text, display scaling, and Magnifier. |
| Motion and time | Motion reinforces, but never solely communicates, state. Feedback remains visible long enough to understand. | Confirm the static state tells the same story as the animation. |
| Custom controls | A custom control provides correct AutomationPeer support, role, name, state, keyboard activation, focus behavior, and contrast-safe rendering before it replaces a native control. | Inspect the UI Automation pattern; test with Narrator and keyboard. |

### Wino-specific accessibility decisions

- **Message lists:** Expose sender, subject, time, unread/flagged state, attachment state, and selection in a meaningful announcement. Do not rely only on color or bold weight to convey unread state.
- **Reading and compose:** Preserve a predictable order from message header to body to attachments and actions. Embedded WebView/editor content needs an equivalent accessible route and clear loading state.
- **Calendar:** Expose date, time, title, calendar identity, and conflict/availability in text. Users can move, create, edit, and delete events without drag-only interaction.
- **Settings:** Use visible labels plus plain-language helper/error text. Explain impact before an irreversible or privacy-affecting change.
- **Intelligence:** State availability, progress, origin, and limitation in text. Local/cloud coverage, confidence, and failure never rely only on color or animation.

### Accessibility acceptance checklist

1. Give every interactive element an accessible name; give every icon-only control an accessible name and tooltip.
2. Complete the primary scenario using only a keyboard, including overlay dismissal and focus return.
3. Use Narrator to check headings, lists, selected values, status changes, errors, and custom controls.
4. Test Light, Dark, and a Windows Contrast theme; ensure no semantic information vanishes or relies only on color.
5. Verify actual text contrast at 4.5:1 or higher, including personalized-theme combinations.
6. Check enlarged text, display scale, and Magnifier for clipping, overlap, lost labels, and unreachable commands.
7. Run `scripts/audit-xaml-automationids.ps1` and exercise the deployed Debug app with WinApp CLI after Visual Studio deploys it.

## 12. WinUI XAML implementation decisions

- Existing semantic resources are the design tokens. Search the shared resources and control library before adding a style.
- Use built-in WinUI controls and styles unless an existing Wino reusable control owns the interaction.
- Use `{ThemeResource}` for UI-facing colors and brushes. Inside a theme dictionary, use `{StaticResource}` except for system resources that must respond to runtime changes.
- Base custom styles on platform defaults. Do not re-template standard controls just to change a brush, margin, or corner.
- For `x:Bind`, explicitly use `Mode=OneWay` for changing view-model values. Use `UpdateSourceTrigger=PropertyChanged` for text that must update while typing.
- Keep visual composition in XAML. Use semantic resource names and preserve the Light, Dark, High Contrast, keyboard, pointer, touch, and automation behavior of the base control.
- For reusable controls, follow `controls/AGENTS.md` for templates, parts, playground coverage, automation peers, and lifecycle rules.

## 13. New-feature design workflow

1. **Name the task.** State the primary user task, supporting tasks, content density, and feature home: Mail, Calendar, Contacts, Settings, or Intelligence.
2. **Choose the silhouette.** Decide whether it belongs in the existing shell, list-detail workspace, focused editor, or settings flow. Do not start with a control.
3. **Map states.** Specify loading, empty, error/offline, selection, save/sync, privacy, and destructive/recovery behavior.
4. **Choose native patterns.** Map every interaction to a WinUI control and existing Wino style. Explain any custom-control exception.
5. **Write responsive behavior.** Describe large, medium, and small-window behavior, including what moves, collapses, or becomes drill-in.
6. **Specify accessibility.** Identify keyboard flow, focus return, accessible names, automation IDs, Narrator behavior, text scaling, and Contrast-theme behavior.
7. **Build and verify.** Review all themes and state variants. Run the XAML automation-ID audit; after Visual Studio deploys the current build, exercise the changed flow through WinApp CLI.

## Sources

- [Microsoft: Design Windows apps overview](https://learn.microsoft.com/en-us/windows/apps/design/)
- [Microsoft: Windows 11 design principles](https://learn.microsoft.com/en-us/windows/apps/design/design-principles)
- [Microsoft: Design guidelines overview](https://learn.microsoft.com/en-us/windows/apps/design/guidelines-overview)
- [Microsoft: Elevation in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering)
- [Microsoft: Geometry in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)
- [Microsoft: Accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)
- [Microsoft: Accessible text requirements](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements)
- [Microsoft: Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)
- [Microsoft: Accessibility testing](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-testing)
