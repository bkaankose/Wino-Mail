# Wino Mail for Linux: design decisions

Decisions for the GTK 4 and libadwaita app in `src/Wino.Mail.Linux` and the Linux control libraries under `controls/` (issue #1216). Shared ViewModels, services, sync, storage and the reader and editor engine are unchanged by any of them; every section below is a presentation decision. The shell, workflow and naming follow the [macOS design decisions](macos-design-decisions.md) and the [Wino design guideline](wino-design-guideline.md) unless a section here says otherwise. Project rules are in [src/Wino.Mail.Linux/AGENTS.md](../src/Wino.Mail.Linux/AGENTS.md); the developer guide is [Linux development](harness/development-linux.md).

## Tiling profile and Omarchy theme (22 November 2026)

The app targets GNOME on Wayland, and the same binary runs on tiling compositors (Hyprland, Sway, Niri, river, i3) and on Omarchy, an Arch and Hyprland desktop that themes the whole system from one palette. On those desktops the compositor owns window placement, size and closing, and GNOME's window chrome looks out of place.

| Topic | Decision |
| --- | --- |
| Detection | `Wino.Platform.Linux` reads only environment variables and state files: `XDG_CURRENT_DESKTOP` (split on `:`) naming Hyprland, sway, niri, river or i3, or `HYPRLAND_INSTANCE_SIGNATURE`, `SWAYSOCK`, `NIRI_SOCKET`, means Tiling; `$XDG_STATE_HOME/omarchy/current/theme` (or `~/.config/omarchy/current/theme`) means Omarchy. No compositor IPC and no D-Bus. |
| Window style setting | Settings › Personalization › Window style: Automatic (the detected style), Standard, Tiling. Stored under the head-local key `LinuxWindowStyle`; the first window reads it from `preferences.json` before the services start. |
| Window buttons | Hidden for every window at once through the GTK setting `gtk-decoration-layout` (`:`) in the Tiling style, and reset to the desktop's value in Standard. No view adds or removes window buttons itself. Windows close through the compositor. `Adw.Dialog` sheets (About, the folder picker, the message source) were expected to lose their close button too; libadwaita 1.10 keeps it in dialogs, and Escape closes them as before. A dialog must never depend on its header close button alone. |
| Flat look | The Tiling style adds one block to the theme stylesheet: window corners and shadows off, 2 to 4 px control and card radii, thin outlines instead of shadows on cards, boxed lists and popovers. Pills (999 px) stay round. It is part of the one theme provider, not a CSS class on windows, so it covers every window, dialog and popover and switches live. |
| Omarchy palette | Applied only when all hold: Omarchy is detected, Follow the Omarchy theme is on (Settings › Personalization, default on, key `LinuxFollowOmarchyTheme`), the Default Wino theme is selected, no theme-editor preview is shown, and the palette's mode (`mode` in `colors.toml`, or `light.mode`) equals the applied light or dark. Otherwise the app looks exactly as without it. An accent picked in Wino wins over the palette's accent. |
| Palette mapping | Window, view and header bar ← `background`; every foreground ← `foreground`; sidebars ← `dark_background`; cards, popovers and dialogs ← `lighter_background` (else 6 % foreground over background); accent ← `accent`; destructive and error ← `red` (else `color1`), success ← `green` (`color2`), warning ← `yellow` (`color3`). `selection` and `muted` are not mapped. The colours replace libadwaita's named colours in both spellings; `ThemeCss` is the only place that redefines them. |
| Live changes | The theme follows Omarchy without a restart: file monitors on Omarchy's `current` folder (the theme folder is replaced on every switch) and on the theme folder itself, settled for 250 ms. Omarchy flips the desktop's colour scheme itself; the app follows it as on GNOME. |
| Window identity | The program name is the application id, so every window, also Settings and the embedded sign-in browser (not added to the application on purpose), reports the Wayland app id `app.winomail.Mail` for compositor rules. |
| Small tiles | Minimum sizes stay as on GNOME (420 × 420 shell, 360 × 400 Settings, 360 × 240 secondary windows). Content must survive a 460 × 490 tile: below 620 sp the Welcome and account setup footers put their links on a line of their own, and rows of action buttons (setup notices, the Wino Intelligence offer, certificate actions) are `Adw.WrapBox`es that wrap instead of widening the page. These breakpoints only trigger below widths that clipped before, so GNOME layouts are unchanged. |
| Out of scope | The reader and composer web views are not themed from the palette. |

## Keyboard-first navigation (22 November 2026)

The owner asked that shortcuts cover the most used actions and that the UI is easier to navigate with the keyboard. Omarchy and Hyprland users drive everything from the keyboard, and Super with any key belongs to the compositor, so no Wino key uses Super. The same keys stay correct on GNOME.

| Topic | Decision |
| --- | --- |
| One table | Every fixed key is a row of `LinuxKeymap`: registration, the router, the shortcuts overview, tooltips, menu hints and the recorder's conflict check read it. The configurable shortcuts stay in the shared service and Settings. |
| Two kinds of fixed key | Global keys are application accelerators. Keys that depend on the focus (F6, Escape, Alt+Down, Ctrl+J, Ctrl+P, zoom, Ctrl+T, Alt+1 to 4, Ctrl+D, Ctrl+Shift+A) are matched by the window's router after the configurable shortcuts, because accelerators also fire inside dialogs, popovers and entries. A key that does not apply passes on to the focused widget. |
| Window guard | The shell's actions act only while the main window is active, so F9, F5, Ctrl+1 to 4 or Alt+Left in a pop-out or in Settings leave the shell alone. Ctrl+F opens the find bar of the active pop-out; F1 shows the overview over the active window; Ctrl+W closes the active window through its own close request (a visible dialog first). |
| Panes | F6 and Shift+F6 cycle the visible panes: sidebar, content, detail (reader, composer, contact detail, task drawer), trailing panel. The stacked layout offers only the page it shows. While the keyboard moves focus, the pane that holds it gets a thin inner accent line; a click removes it. |
| Escape, Enter, Right | Escape steps from the detail, the trailing panel, an overlay sidebar or the search entry back to the content; in the list it clears the selection as before. Enter on a list row, and Right on a mail row that is not a collapsed conversation, move into the detail once it shows. Enter on a sidebar row moves to the content. |
| Focus never lost | Removing or hiding the focused widget (a sent composer, the reader between messages) put the focus nowhere or on the split; the shell gives it back to the pane that had it. A page change focuses the new content unless the user is in the sidebar list or the header. |
| Next and previous | Alt+Down and Alt+Up move through messages, contacts, tasks and calendar periods; with the reader focused the focus stays in the reader. |
| Reader | The reader's body counts as text input only for Ctrl+A, Ctrl+C and Ctrl+Insert, so Delete, Ctrl+U and the single keys work while reading. Zoom is per reader window and not stored. |
| Single keys | Gmail-style letters for Mail only, acting with the focus in the message list or the reader, never in an entry, the composer or the sidebar (whose type-ahead keeps the letters). A head-local setting (Settings › Keyboard shortcuts), on by default with the Tiling window style and off elsewhere; the user's choice wins. No chords. |
| Forward | Seeded once as the configurable Mail shortcut Ctrl+L, so Settings and the menu hints show it; Reset to defaults seeds it again. |
| Shift+Delete | Runs the mode's Delete shortcut with Shift held; the request coordinator makes it a permanent delete after its confirmation. |
| Dialogs | Alert responses and form dialog buttons get unique Alt+letter mnemonics; Escape and the default response were already right. |
| Left out | A command palette, Go to Inbox, save and discard draft keys, a contact edit key, next and previous in pop-outs. |

| Key | Action | Where |
| --- | --- | --- |
| F6, Shift+F6 | Next and previous pane | Main window, all modes, also from text fields |
| F9 | Show or hide the sidebar and focus it, or the content | Main window |
| Escape | Back to the list (detail, trailing panel, overlay sidebar, search); clear the selection in the list | Main window |
| Enter, Right | Open the selected item into the reader, composer, contact detail or task drawer | Lists |
| Alt+Down, Alt+Up | Next and previous message, contact, task or calendar period | Main window |
| Alt+Left | Back | Main window |
| Ctrl+1 to Ctrl+4 | Mail, Calendar, People, To Do | Main window |
| F5 | Synchronize | Main window |
| Ctrl+F | Search, or find in the active pop-out | Main window and pop-outs |
| F1, Ctrl+? | Keyboard shortcuts overview | Main window and pop-outs |
| Ctrl+comma | Settings | Main window and pop-outs |
| Ctrl+W | Close the active window (its dialog first) | Main window and pop-outs |
| Ctrl+Q | Quit | Main window and pop-outs |
| Ctrl+N, Ctrl+R, Ctrl+Shift+R, Ctrl+L | New mail, reply, reply all, forward (configurable) | Mail |
| Ctrl+U, Ctrl+Shift+G, Ctrl+Shift+A, Ctrl+Shift+V, Ctrl+Shift+T | Read or unread, flag, archive, move, create task (configurable) | Mail |
| Delete, Shift+Delete | Delete, delete permanently | Mail |
| Ctrl+Enter | Send (configurable) | Composer |
| Ctrl+J | Junk or not junk | Mail list and reader |
| Ctrl+O | Pop out the reader or the docked composer | Mail |
| Ctrl+P | Print the message | Reader, also popped out |
| Ctrl+plus or Ctrl+equal, Ctrl+minus, Ctrl+0 | Zoom in, zoom out, reset zoom | Reader, also popped out |
| Ctrl+Shift+A | Attach files | Focus in the composer, also popped out |
| Ctrl+T | Today | Calendar |
| Alt+1 to Alt+4 | Day, Week, Work week, Month | Calendar |
| Ctrl+D | Complete the selected tasks | To Do |
| j, k | Next and previous message | Mail, single keys on |
| r, a, f | Reply, reply all, forward | Mail, single keys on |
| e, #, u, s, v | Archive, delete, read or unread, flag, move | Mail, single keys on |
| /, ? | Search, shortcuts overview | Mail, single keys on |

The Settings window is not an application window: it keeps its own Ctrl+W and Escape, and the application keys do not reach it. The overview also lists keys GTK widgets handle themselves: Ctrl+A and Ctrl+Z in the list, Left and Right on conversations and calendar periods, Menu and Shift+F10, F10, and Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+K, Ctrl+F and Ctrl+H in the composer.
