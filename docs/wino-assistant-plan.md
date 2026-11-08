# Wino Assistant plan

Status: agreed design, not built (8 Nov 2026), on branch `feature/wino-assistant`. It extends the voice commands (voice bubble, Personal mode brain, pickers) into **Wino Assistant**: typed or spoken requests in Private or Personal mode. Requests started from selected items (section 5) are designed but deferred. All of it ships as one Wino.Mail.Contracts release; Windows and macOS are built side by side, iOS and Android follow.

## 0. One shared assistant, thin platforms

The same request must plan, ask, resolve and run the same way on every client. This is the rule every change in this plan is checked against.

- **Shared, and the only place with assistant logic:** the server planner and mapping; on the client the plan contracts, the question service (pickers, device-side matching), the preview builder, the plan executor, the brain service, the voice command session (text and audio), quota and fallback handling, and the bubble's behaviour (`VoiceBubbleController` in `Wino.Mail.ViewModels`). These live in `Wino.Core.Domain`, `Wino.Services`, `Wino.Core` and `Wino.Mail.ViewModels`.
- **Platform-specific, with no decisions in it:**
  - Microphone capture (`IMicrophoneService`): the one real platform feature of the assistant.
  - Native drawing of the bubble view, bound to the shared controller (`IVoiceBubbleHost`, `IVoiceBubbleView`).
  - Global shortcut registration.
  - The window navigation behind `IVoicePlanHost`: open a search, a mail, a draft, an event or the editor.

  Each is an adapter that forwards to shared code. A platform never filters a plan, matches a name, words a step or decides a fallback.
- When a platform needs behaviour that isn't in the shared layer, it is added to the shared layer for every platform, not written in the head.
- The voice runner (`tools/Wino.Intelligence.Console`) exercises the shared pipeline only, so its accuracy results hold for every client. A platform-only bug is by definition in one of the four adapters above.

## 1. Product rules

- Every assistant request needs a Wino Account and the Wino Intelligence subscription. There is no free tier.
- Private mode: the planner sees only the request. Personal mode: the planner also sees the brain. Neither ever sees mail, event or task content.
- The planner proposes actions the user could perform in the app by hand; the app shows the plan and runs it after confirmation.
- The brain grows from the user's answers. Anything semantic about people or services (my bank, my boss, the team) enters the brain only by answering a question, asked once in Personal mode.

## 2. Assistant bubble

The voice bubble becomes **Wino Assistant** (control renamed with it).

- Two shortcuts, both on the Voice Control (to be: Assistant) settings page: the existing one opens the bubble in voice mode, a new one opens it in text mode.
- Text mode looks and animates like voice mode (the rising and falling text, the same layout), but the user types with a caret. It is a styled editable text block, not a plain WinUI `TextBox`.
- Text mode never connects to the transcription provider. The client says `text` or `audio` in the start frame; a text request opens no upstream and is charged to the assistant quota only.
- The bubble in text mode shows a microphone button. Pressing it switches to voice: transcribed words are appended to the typed text as the user speaks, and the request is sent as a voice request.
- When the voice quota is used up, opening in voice mode falls back to text mode automatically, with a short note.

## 3. Quota and cost

### Quota

One allowance shown to users, two buckets internally:

> **Assistant: 200 requests a month, up to 50 of them spoken.**

| Bucket | Charged when | Limit |
|---|---|---|
| Assistant | Every request that reaches the planner: typed or spoken, with or without context, Private or Personal | 200 / month |
| Voice | Audio is transcribed | 50 / month |

A spoken request uses one of each. A higher Intelligence tier (for example 10 USD a month) may raise both later. Rename the API bucket `Voice` to `Assistant` and add the `Voice` sub-bucket; the settings and usage pages show the single sentence above.

### Measured cost (8 Nov 2026)

Prices: `claude-haiku-5-5` input 0.10, output 0.50, cache read 0.01, 5-minute cache write 0.125, 1-hour cache write 0.20 USD per million tokens (Anthropic model table cached 6 Oct 2026); `gpt-live-transcribe` 0.017 USD per audio minute (OpenAI pricing). Tokens from 2410 evaluation requests per mode (`tools/Wino.Intelligence.Console` voice runner):

| | Cached prompt | Uncached input | Output |
|---|---|---|---|
| Private | 23,410 | 110 | 253 |
| Personal (brain after the cache breakpoint) | 26,383 | 1,790 | 257 |

Planner cost per request:

| | Cache warm | Cache cold (5-minute write) |
|---|---|---|
| Private | 0.00037 USD | 0.0031 USD |
| Personal | 0.00057 USD | 0.0036 USD |

Production's own usage audit (25 real requests, 30 days) averages 0.00099 USD per planner call: a mix of warm and cold, because the prompt cache (5 minutes, shared by all users) expires between sparse requests.

Transcription: about 0.0023 USD for 8 seconds of audio, 0.013 USD at the 45-second maximum. Transcription is not in the usage audit today (no rows); add it (audio seconds priced per minute) so the dashboard and the audit agree.

Per user at the full allowance (150 typed + 50 spoken, 8 s each, Personal mode): about 0.23 USD a month with a warm cache, about 0.83 USD with every request cold. If all 50 spoken requests ran the full 45 seconds, transcription alone would be 0.64 USD.

Cost levers:

- **1-hour cache TTL on the system prompt breakpoint.** With sparse traffic most requests pay the 5-minute write (0.0029 USD each). A 1-hour entry costs about 0.0047 USD per hour to keep (one write, refreshed free by reads), so above about two requests an hour across all users it is cheaper, and every request then costs the warm price. Recommended now.
- The brain is about 1,800 uncached tokens (0.00018 USD per Personal request). Caching it per user only pays when one user sends several requests within the TTL; not worth a breakpoint. Removing events (section 4) shrinks it.
- Transcription is the largest share of a spoken request once the cache is warm (about 80%), which is why voice gets the smaller bucket.

## 4. The brain

### Automatic structure (pushed by devices, "no change, no sync")

- Per mailbox: address and provider, sender name, the account's display name and order in Wino, calendar/tasks enabled, folders, calendars, task lists, send-as addresses, categories, capabilities, top 15 contacts (display name and SHA-256 hash of the address).
- Contacts exclude the user's own addresses and aliases; entry and exit use hysteresis so a rank change doesn't push.
- **Events are removed.** Event commands are resolved on the device against the full calendar, as in Private mode (which scored better).
- Sync rules (built 7 Nov, uncommitted): syncs only mark an account dirty; dirty sections rebuild at most every 30 minutes or when an assistant request starts and the last rebuild is older than 5 minutes; a section is sent only when its canonical value hash changes; identical facts are not re-sent; nothing is sent at app start. "What Wino remembers" hides sections and facts of accounts no longer on the device.

### Learned facts (asked once in Personal mode, then remembered)

Each fact stores a concrete target. The brain shows the planner only an alias and the user's words; the server maps the alias back to the target without guessing.

| Question | Fact | Target | Used by |
|---|---|---|---|
| Who is your accountant / mom / boss? (exists) | Person | Address hash | Recipients, attendees, senders |
| Which account is "work"? (exists) | Account nickname | Mailbox | Any command naming the account |
| Which folder is "bills"? (exists) | Folder synonym | Folder id | Mail selections, move targets |
| Default calendar (exists) | Default calendar | Calendar id | New events |
| Who is "my bank"? (pick a sender) | Sender alias (new) | The sender's domain (stored because the user named it) | "what did my bank send", "archive the bank mails" |
| Who is "the team"? (pick several people) | Group (new) | Set of address hashes | Drafts, invitations |
| Which account should new tasks / events / mails use? (asked when ambiguous) | Default account per action (new) | Mailbox | Creates without an account |
| What are "newsletters"? (answer: mails from services) | Filter synonym (new) | The device's sender-kind filter | Mail selections |

Learning from corrections in the confirmation is deferred (it depends on context).

### Device intents (nothing sent to the server)

- **Sender kind** filter on mail selections: Person, Service, Newsletter. The device classifies with Gmail category folders, automated-address heuristics and List-Unsubscribe.
- **Free slots**: create and move commands may carry a slot request (day, part of day or window, length, earliest or latest); the device picks the first gap that fits across all calendars, excluding the event being moved.
- **Request context**: week start and working hours travel with each request (not stored).

## 5. Context-aware requests (deferred)

**Implementation deferred.** This section records the agreed privacy and planning model only. The in-app interactions (entry points, the selection preview, how selections reach the assistant) will be planned separately, and no app or UI changes are made for context-aware requests until then. The rest of this plan does not depend on it.

Started from a selection (right-click, "Wino Assistant") with one or more mails, events, tasks, contacts or a folder. The bubble shows a compact preview of the selection, drawn on the device.

- The request carries a context block: item kind, count and plan-relevant flags (read, flagged, has attachments, folder type, invitation, organizer, has attendees, recurring, all-day, overdue, can unsubscribe, ...). In Personal mode it also carries brain aliases of the people involved; anyone not in the brain is "an unknown person". Never subjects, bodies, titles or task text.
- A flag in the request selects a smaller contextual prompt without account, folder and brain-matching rules.
- Commands act on the selection with `usesContext`, and address its people with device-expanded tokens: `@context.sender`, `senders`, `recipients`, `replyAll`, `attendees`, `organizer`, `contacts`, and `@result.N.*` for earlier commands.
- The server enforces the allowed actions for the selection's kind and rejects a contextual plan that acts on anything else than the selection (or creates something new from it).
- Content-needing actions (summarize, translate, analyze, copy code) are app actions the device triggers through Wino Intelligence; the planner only decides to trigger them.

## 6. Actions

Everything the app can already do by hand, grounded in existing commands, context menus and request types (catalog researched 8 Nov 2026). New items need a contract `VoiceAction`.

- **Mail** (all multi-select): read/unread, flag, archive/unarchive, delete; move to folder; junk / not junk; Focused/Other and the "always" rule (Outlook); pin; categorize; reply, reply all, forward; send drafts; summarize; translate; analyze now; copy verification code; unsubscribe; save as PDF/EML; print; create task or event from it; undo.
- **Event**: open, open series, move/shift/resize/rename/relocate (single or series), show as, respond with message, set reminder, join online, edit in the editor, delete, mail attendees or organizer, create task from it.
- **Task**: complete/reopen, importance, due date, My Day, rename/notes, add step, move to list, delete, create event from it; lists: create, rename, delete; find tasks.
- **Contact**: mail, create event with, favorite, add to/remove from list, categorize, open, create, delete, find, find their mails.
- **Folder**: mark all read, empty, pin, rename, create subfolder, delete, sync now.
- **No context**: everything above that starts from a search (find mails, events, tasks, contacts), plus sync and undo.

Decisions:

- **Drafts and sending**: creating drafts stays the default. Sending is supported with confirmation ("send all these drafts" from the Drafts folder). Forward asks for the address when the request doesn't give it.
- **Unsubscribe**: no extra confirmation, also for several selected mails; mails that can't be unsubscribed are skipped silently. A single mail that can't be unsubscribed is a no-op with a short note.
- **Mail rules**: excluded.
- **Excluded**: hard delete as its own action (only the existing permanent-delete guard), smart actions that open links from the mail body (pay, sign, track; only copy code and unsubscribe qualify), dead or placeholder commands (Ignore, email/print list, duplicate event, call contact), editing steps by number, settings-level changes.
- Risk: existing second-confirmation guards stay (large or permanent deletes, emptying folders, large archives, decline several meetings, meetings with attendees), plus send, delete task/contact/folder, and the "always Focused/Other" rule.

### Chaining

Commands chain across kinds (`usesResultOf` and `usesContext`), up to **5 commands** per plan. Examples: event -> draft to attendees -> show as tentative; mail -> task for Friday -> archive; unsubscribe -> delete; find overdue tasks -> due tomorrow; find contact -> find their mails -> mark read. Accuracy comes first: every chain pattern gets corpus cases before it ships.

## 7. Contract changes (one release)

- Start frame: input kind (`text` | `audio`), week start, working hours. The context block (kind, count, flags, people aliases) and `UsesContext` wait for section 5.
- `VoiceCommand`: chaining by kind table; `MaxCommands` 5; new actions per section 6; `DraftDetails.Kind` (New, Reply, ReplyAll, Forward); send-draft action; move target folder; category; language; `EventChange.Scope`, `ReminderMinutes`; slot request on events and changes; task, contact and folder selections and changes; recipient tokens for earlier results (`@result.N.*`; the `@context.*` tokens wait for section 5); `MailSelection.SenderKind`; `MissingDetail` additions (Folder, Category, Language, TaskList, Task, Contact).
- Brain: section `Name` and `Order`; events removed; fact kinds `SenderAlias`, `Group`, `DefaultAccount`, `FilterSynonym`.
- Quota: `Assistant` bucket with a `Voice` sub-bucket; transcription usage audited.

## 8. Verification

The voice runner (`tools/Wino.Intelligence.Console`, `voice` mode) runs every prompt through the app's real pipeline with nothing executed. Each feature above gets corpus cases (both modes, context and no context, chains, other languages) before it ships; the API test suite needs `WINO_TEST_POSTGRES` set.
