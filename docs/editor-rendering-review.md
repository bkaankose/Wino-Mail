# Wino.Editor rendering review

Scope: `controls/Wino.Editor` (`Editor/reader.html` + `reader.js` + `reader.css` for reading, `Editor/editor.html` + `editor.js` + `editor.css` for composing), the WebView2 hosts (`WinoMailRenderer.xaml.cs`, `WinoMailEditor.xaml.cs`, `EditorBridge.cs`, `EditorAssetProvider.cs`), and the app-side HTML preparation that feeds them (`HtmlPreviewVisitor`, `MimeFileService.GetMailRenderModel`).

Goal: render mail as close to Outlook / Gmail as possible, keep the sender's colors in dark mode where that is what the sender intended, and do it without loosening the script/CSS security posture.

## How rendering works today

1. `HtmlPreviewVisitor` (MimeKit `HtmlToHtml`) strips `script`, `iframe`, `object`, `embed`, `base`, `meta`, `form`, `link`, all `on*` attributes, and non-http/https/cid resource URLs. `MimeFileService` optionally clears `img src` (junk folder / images blocked) and styles.
2. `WinoMailRenderer` base64-encodes the result and calls `WinoRenderer.render`. `reader.js` runs DOMPurify (html profile, extra `FORBID_TAGS`), optionally Readability, then sets `#wino-reader.innerHTML`. Everything lives in the same document as the reader chrome. No `<head>` from the mail survives.
3. Theme: `setTheme(isDark)` flips `data-theme`, sets `Profile.PreferredColorScheme`, and **enables Dark Reader 4.9.1 dynamic mode** (`brightness 100, contrast 90`) over the whole document. Dark Reader rewrites every background, text, border and gradient color by HSL remapping (`modifyBgHSL` / `modifyFgHSL`), injects override stylesheets, and tags inline styles with `data-darkreader-inline-*`.
4. The composer (`editor.html`) does the same Dark Reader trick over the `contenteditable` surface and strips the Dark Reader attributes back out in `getContent()`.

## Why colors get lost in dark mode

Dark Reader is a general-purpose page inverter, not a mail dark-mode engine. Its color mapping is destructive by design:

- **Every color is remapped, not just "light background / dark text".** A brand-red button, a green "Approved" badge, a colored table header all go through `modifyBgHSL`, which clamps lightness and desaturates near-neutral colors (`sNeutralLim 0.24` forces neutral colors to a warm 40° hue with 10% saturation). That is why colored newsletters look muddy or brownish in Wino's dark mode and untouched in Outlook.
- **Mails that already ship a dark theme get double-processed.** The visitor deletes `meta` and DOMPurify drops `<head>`, so `<meta name="color-scheme" content="light dark">` and `<meta name="supported-color-schemes">` never reach the page. `@media (prefers-color-scheme: dark)` blocks inside body `<style>` do fire (because `PreferredColorScheme` is set) and produce the sender's dark palette, and then Dark Reader remaps that palette again. Outlook and Gmail detect these hints and *skip* their own inversion.
- **Images are not part of the decision.** Dark Reader analyzes `background-image` pixels (`analyzeImage`) but never `<img>` content, so a white PNG logo on a now-dark table cell, or a dark text-as-image on a now-dark background, becomes invisible. Outlook keeps the original background behind image-heavy blocks for exactly this reason.
- **Text-in-color contrast is not evaluated as a pair.** Dark Reader remaps foreground and background independently. Combinations the sender tuned (light text on a saturated background) can land as light-on-light.
- **Version.** Dark Reader 4.9.1 is a 2020 build. It predates `color-mix()`, CSS nesting, `oklch`, `light-dark()`, and many fixes for gradients and `background-clip: text`. Mails built with modern tooling hit unhandled syntax and fall back to unmodified light colors on a dark surface.

## What Outlook and Gmail do instead

- **Gmail (web) does not recolor message bodies at all.** In dark theme the message card stays the sender's colors on the sender's background; unset backgrounds fall back to a light card. Zero fidelity loss, no attempt at a dark mail.
- **Outlook (desktop / OWA) offers per-message dark mode with an opt-out sun icon.** Its algorithm only inverts when a region is "light background + dark text", leaves saturated brand colors and images alone, respects `color-scheme` / `supported-color-schemes` meta and `prefers-color-scheme` CSS, and never touches a mail that declares its own dark styles.
- **Apple Mail** does partial inversion: backgrounds only if the mail has no full-bleed background, and text only where contrast would otherwise fail.

The common thread: **detect intent first, invert selectively second, and give the user a per-message escape hatch**. Wino has none of the three.

## Recommendations for `reader.html`

Ordered by expected fidelity gain per unit of work.

### R1. Isolate the mail in a sandboxed `<iframe>` instead of `innerHTML` into the host document

Both Gmail and Thunderbird render the body in its own frame. Benefits that directly address the "renders differently than Outlook/Gmail" complaint:

- The mail gets its own `<html><head><body>`, so `<head><style>` blocks, `<body bgcolor>`, `<body style="margin:0">`, `<meta name="color-scheme">` and `<meta name="viewport">` behave the way the sender tested them. Today anything in `<head>` is discarded and `body` selectors in the mail's CSS hit **Wino's** body.
- Mail CSS can no longer restyle the reader chrome (`* { }`, `body { }`, `a { }`, `#wino-reader` collisions), and Wino's `reader.css` no longer bleeds into the mail (see R3).
- `sandbox=""` (no `allow-scripts`, no `allow-same-origin`, no `allow-forms`, no `allow-top-navigation`) is the strongest script isolation the platform offers, on top of DOMPurify. A CSP `<meta>` inside the `srcdoc` (`default-src 'none'; img-src https: http: cid: data:; style-src 'unsafe-inline'; font-src 'none'`) closes off CSS `@import`, remote fonts, and `url()` beacons that DOMPurify does not filter because it does not parse CSS.
- Auto-height: post the document's `scrollHeight` from the frame via a tiny host-side script, or use the `wino-reader` root as the scroller and size the frame to content. Link clicks still reach the host through `NavigationStarting` on the WebView (the frame cannot navigate top with this sandbox, so the click handler moves to a `postMessage` from a `document` listener injected by the host with `AddScriptToExecuteOnDocumentCreatedAsync`, or the frame is given `allow-top-navigation-by-user-activation` and the existing handler keeps working).

If a frame is too large a change for one step, a **shadow root** on `#wino-reader` gives CSS isolation (both directions) with the same `innerHTML` flow, but does not restore `<head>`/`<body>` semantics and does not add a sandbox. Prefer the frame.

### R2. Keep the mail's `<head>` styles and color-scheme hints

Independent of R1, today's pipeline drops them twice:

- `HtmlPreviewVisitor.BlockedTags` deletes `meta` wholesale. Allow-list `meta[name=color-scheme]`, `meta[name=supported-color-schemes]` and `meta[charset]`; keep blocking `http-equiv`, `refresh` and anything else.
- `reader.js` sanitizes with DOMPurify's default `WHOLE_DOCUMENT: false`, which returns `body.innerHTML` and throws away head `<style>`. Set `FORCE_BODY: true` (keeps leading `<style>` in the body) or, with R1, `WHOLE_DOCUMENT: true` and hand the full document to the frame. `ADD_TAGS: ['meta']` with `ADD_ATTR: ['name','content']` for the two allowed meta names.

Without this, any mail with `<head><style>` (most Outlook-authored and most ESP-authored mails) renders unstyled and is guaranteed to differ from Outlook.

### R3. Remove reader CSS that rewrites the mail's layout

`reader.css` currently applies to the mail content and changes layout in ways neither Outlook nor Gmail do:

| Rule | Effect on real mail |
| --- | --- |
| `img { max-width: 100%; height: auto }` | `height: auto` beats the `height=""` attribute. Spacer images (`width=1 height=20`) collapse to 1 px and vertical rhythm in table layouts disappears. Fixed-size hero images that were meant to overflow get squeezed. Gmail only sets `max-width` on the *container*, not on `img`. |
| `table { max-width: 100% }` | Fixed 600/640 px newsletter tables are squeezed in narrow reading panes and nested tables wrap into columns the sender never designed. Outlook lets the mail scroll horizontally. |
| `body { overflow-wrap: anywhere }` | Breaks words mid-word everywhere in the mail, including inside `nowrap` cells and buttons. Outlook/Gmail use `overflow-wrap: break-word` at most. |
| `#wino-reader { font-family; font-size: 15px }` | Reasonable as a default, but it becomes the base for `em`/`%` sizes in the mail. Outlook's base is 16 px, Gmail's 13 px Arial for unstyled text. Keep the user's font, but only as a fallback on unstyled text (`:where(#wino-reader)` so any mail rule wins), and apply the size to the *reader UI* rather than as the mail's root font size. |
| `pre { white-space: pre-wrap }` | Fine, but scope it to `.wino-reader` (Readability mode) like the other rules. |

Move all of these under `.wino-reader` (Readability mode) and leave the `Original` mode untouched except for `img { max-width: 100% }` **without** `height: auto`, applied only when the image has no explicit `width`/`height` attributes (`img:not([width]):not([height])`).

### R4. Replace whole-document Dark Reader with a selective, intent-aware dark mode

Keep Dark Reader as the low-level color engine if you like its HSL mapping, but change the policy around it:

1. **Detect sender intent and skip inversion when present.** If the sanitized document contains `meta[name=color-scheme]` with `dark`, `meta[name=supported-color-schemes]` with `dark`, or a `@media (prefers-color-scheme: dark)` rule, do not enable Dark Reader. Set `color-scheme: light dark` on the frame's root and let the sender's dark CSS run. This alone fixes the "double-inverted" class of mail.
2. **Only invert "document-like" mail.** Heuristic used by Outlook and Apple Mail: sample the effective background of the body and of the top-level blocks. If the mail has no explicit background colors at all (plain replies, transactional text mail) invert fully. If more than a small share of the visible area carries an explicit non-white background, an image background, or the body sets a background, leave the mail in light mode on a light card (Gmail behaviour). A lighter variant: invert only elements whose computed background is near-white and whose text is near-black, and leave every saturated color untouched (Outlook behaviour). Dark Reader supports this partially through its `ignoreInlineStyle` / `ignoreImageAnalysis` fixes and by passing selectors in `fixes`; implementing it directly as a small color walker over the sanitized DOM is simpler and predictable.
3. **Preserve saturation.** If Dark Reader stays, raise `sNeutralLim` behaviour by passing a theme with `mode: 1` and adjust `modifyBgHSL` / `modifyFgHSL` through a fork so that colors with saturation above ~0.3 keep hue and saturation and only lightness is clamped. Today the code path forces neutral colors to hue 40°, which is the brown cast users notice.
4. **Per-message toggle.** Add a "Show in original colors" button on the reading header (Outlook's sun icon). Wire it as a third `HtmlMailRenderMode` or as a `SetThemeAsync(isDark, invertMail: false)` overload so the host WebView stays dark but the mail card renders light. Persist the choice per message or per sender if desired.
5. **Update Dark Reader** to a current 4.9.x build if it is kept. Fixes gradient, `color-mix`, nested CSS and `background-clip: text` handling, all common in modern ESP output. Record the version and hash in `ThirdParty/THIRD-PARTY-NOTICES.md` like the other vendored scripts (Dark Reader is currently not listed there).

### R5. Harden the surface while widening fidelity

None of the above needs weaker sanitization. Two gaps exist today regardless:

- **No CSP in either document.** `NavigateToString` documents run with a null origin, but nothing stops mail CSS from `@import`ing or pulling `url()` fonts/backgrounds from arbitrary hosts, which is a tracking and fingerprinting channel that DOMPurify does not filter. `EditorAssetProvider` can inject a `<meta http-equiv="Content-Security-Policy">` with a per-load nonce on its inline `<script>` tags: `default-src 'none'; script-src 'nonce-…'; style-src 'unsafe-inline'; img-src https: http: data: cid:; connect-src 'none'; font-src 'none'; frame-src 'none'` (frame-src becomes `srcdoc:`/`'self'` under R1).
- **WebView2 settings are left at defaults** in both hosts. Set `AreDevToolsEnabled = false`, `AreDefaultScriptDialogsEnabled = false`, `AreHostObjectsAllowed = false`, `IsGeneralAutofillEnabled = false`, `IsPasswordAutosaveEnabled = false`, `AreBrowserAcceleratorKeysEnabled = false` (reader), `IsStatusBarEnabled = false`. Consider `AddWebResourceRequestedFilter("*", All)` plus a handler that allows only `https`/`http` image and `cid` requests when images are enabled, and nothing when they are blocked. Today image blocking is done by stripping `src` in `MimeFileService.ClearImages`, which misses CSS `background-image: url()` and `<td background>` after DOMPurify (the visitor sanitizes `background` attributes but not CSS).
- DOMPurify config is good (`USE_PROFILES: html`, explicit forbids). Add `FORBID_ATTR: ['id']` or `SANITIZE_NAMED_PROPS: true` to avoid DOM clobbering of `wino-reader`/`wino-body` ids, and `ALLOW_UNKNOWN_PROTOCOLS: false` (default) stays.

## Recommendations for `editor.html`

The composer has different priorities: what the user sees while typing must match what the recipient receives, and quoted content must not run.

### E1. Sanitize `setContent` input with DOMPurify

`editor.js` `setContent` assigns untrusted HTML straight to `editor.innerHTML`. Reply and forward bodies come from the same visitor output as the reader, which strips `on*` attributes and script tags, so today this is defence-in-depth rather than an open hole. It is still the only surface in the control that renders mail-derived HTML without DOMPurify, and templates, signatures and pasted HTML also flow through it. Bundle `dompurify` into the editor document (it is already embedded for the reader) and run it in `setContent` and in `normalizePastedHtml`, which currently relies on a hand-written `on*`/tag stripper that does not cover `javascript:` in `style` `url()`, `srcdoc`, `formaction`, or namespace confusion.

### E2. Stop enabling Dark Reader over the compose surface

Composing inside an inverted canvas creates several fidelity problems:

- The author picks "Blue" from the color menu and sees a Dark Reader-lightened blue, then sends the original hex. What the recipient sees is never what the author saw.
- Quoted original content is inverted while editing, so the author cannot judge how the reply looks against the quoted mail.
- `getContent()` strips only six `data-darkreader-inline-*` attributes; Dark Reader 4.9.1 also emits `-bgimage`, `-boxshadow`, `-fill`, `-stroke`, `-outline`. Any of those leaks into the sent mail as dead attributes and `--darkreader-inline-*` custom properties.
- Dark Reader also injects `<style class="darkreader">` elements. `getContent` clones only `#wino-editor` so these do not leak, but `getBodyContent` re-parses the full document string and would carry a stray `<style>` if one were ever inside the editor node.

Preferred model (what Outlook and Gmail do): the compose surface is always a light card, even in a dark app theme. The chrome around it is dark. If a dark writing surface is wanted, invert only the **new** text region with a fixed mapping (`color-scheme: dark` on the editable root plus default text/background swap) and leave quoted content and any explicitly colored spans untouched. Either way, remove the Dark Reader dependency from the editor bundle; it also removes ~130 KB from the assembled document and its 2 MB `NavigateToString` budget.

### E3. Emit mail-client-friendly HTML

Small changes in what the editor produces make Wino's outgoing mail render more consistently in Outlook and Gmail:

- Inline the editor's defaults on the outgoing root: wrap the body in `<div style="font-family: …; font-size: …px; color: #1b1b1b">` using the composer typography, instead of relying on `#wino-editor` CSS that does not travel with the mail. Today a mail composed in Calibri 14 arrives with the recipient's default font.
- `codeBlockStyles` is already inlined, which is the right pattern. Do the same for lists and blockquotes the editor creates (`margin`, `padding-inline-start`) and for the quoted-reply separator.
- Make `applyStyle` and `exec("foreColor")` write hex colors, not `rgb()`, and never write `var(--…)` values; check output after Dark Reader removal.
- Keep `color-scheme: light` on the outgoing document only if a dark variant is not authored; do not emit `color-scheme` meta at all otherwise, so recipients' clients apply their own dark mode logic.

### E4. Mirror the reader's isolation for quoted content

When R1 lands, the reply's quoted block can be rendered read-only inside the same kind of sandboxed frame below the editable area (Gmail's "…" collapsed quote). That removes the need to sanitize quoted CSS into the editable DOM at all and keeps the quoted mail pixel-identical to how it looked in the reader.

## Suggested order

1. R3 (CSS rewrite) and R2 (keep head styles and meta) — small, contained in `reader.css`, `reader.js`, `HtmlPreviewVisitor`. Largest fidelity gain for the least risk. Add a playground page with a fixture set (Outlook-authored mail, ESP newsletter with 600 px tables and spacer GIFs, a mail with `prefers-color-scheme` dark CSS, plain-text reply) and compare light and dark against Outlook screenshots.
2. R4 steps 1 and 4 (skip inversion when the mail declares dark support, per-message toggle). No Dark Reader changes needed yet.
3. R5 (CSP, WebView2 settings, resource filter). Independent of the others.
4. E1, E2, E3 in the composer.
5. R1 (iframe isolation), then E4, then R4 steps 2, 3 and 5 as the long-term rendering model.

## Verification

Prose only in this review; no build or runtime check was run. Any implementation should go through the playground states in `controls/AGENTS.md`, the `winapp ui` screenshot audit in both themes, and the `Wino.Mail.Controls.Tests` sanitizer fixtures once they exist.

## Implementation status (2026-09-25)

| Item | Status |
| --- | --- |
| R1 isolation | Done with a shadow root instead of an iframe. Chromium cannot split an iframe across printed or PDF pages, and Wino prints the reader. Script isolation comes from DOMPurify plus the new nonce CSP. |
| R2 head styles and color-scheme meta | Done. The visitor keeps only `color-scheme` and `supported-color-schemes` meta tags. The reader keeps head and body `<style>` and rewrites `html`, `body` and `:root` selectors. |
| R3 layout-altering reader CSS | Done. Original mode no longer forces `height: auto`, table `max-width`, or `overflow-wrap: anywhere`. |
| R4 dark mode | Done. Dark Reader is removed. Sender dark styles are used when declared, `light only` is honored, and other mail gets the selective `mail-colors.js` engine. The existing per-message reader theme toggle is the "original colors" switch. |
| R5 hardening | Done. Nonce CSP in both documents, WebView2 settings hardened, and `BlockRemoteResources` blocks requests at the network layer for junk and image-blocked mail. Accelerator keys were left on so Ctrl+F and print keep working. |
| E1 composer sanitization | Done for content loading, `insertHtml`, and paste. |
| E2 composer dark mode | Changed approach. The app exposes a dark writing surface toggle, so it now uses the shared engine through removable attributes instead of a forced light card. |
| E3 outgoing HTML | Done for typography root, list and quote spacing, and hex colors. The default text color is not inlined, so recipients' dark modes can still adapt it. |
| E4 quoted content frame | Not done. It depended on the iframe model that R1 rejected for printing reasons. |
