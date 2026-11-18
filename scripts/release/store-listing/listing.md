# Microsoft Store listing (en-US)

Draft copy and images for the Store page of Wino Mail (`9NCRCVJC50WL`). Paste the text into Partner Center > Store listings > English (United States).

Exchange Server, Wino Assistant, Privacy & security and account groups shipped in 2.2.13. Publish this listing together with or after that version.

## Product name

Wino Mail & Calendar

## Short description

Fast, native Windows app for mail, calendar, people and to-dos. Outlook, Gmail, iCloud, Exchange, IMAP and POP3 in one calm inbox.

## Description

Wino Mail is a native Windows app for your mail, calendar, contacts and tasks. It is not a web wrapper. It starts fast, stays responsive and uses little memory on x64 and ARM64. Wino syncs directly with your providers and keeps your mail on your PC.

Wino has four modes: Mail, Calendar, People and To Do. Switching between them is instant, and each one has its own Start menu entry.

WINO MAIL
• Connect Outlook, Gmail, iCloud, Exchange Server and any IMAP or POP3 account
• Sign in to IMAP accounts securely in your browser, with no app password
• Combine accounts into account groups with one inbox, or keep each account separate
• Act on mail without opening it, with hover actions, swipe actions and searchable context menus
• Sort new mail automatically with mail filters
• Answer event invitations straight from the message
• Read messages in dark mode and arrange the reader's buttons the way you want
• Write with aliases, signatures, templates and rich formatting

WINO CALENDAR
• See Outlook, Google, Exchange and CalDAV calendars in one view
• Use day, week, work week and month views that open at the current time
• Create and edit events, recurring series and reminders
• Glide through days, weeks and months with mouse, pen or touch

WINO PEOPLE
• Manage contacts from all your accounts in one place
• Organize them with address books, lists, categories and favorites
• Keep several email addresses, phone numbers, addresses, birthdays, notes and photos per contact
• Sync through Outlook, Gmail, Exchange and CardDAV

WINO TO DO
• Plan with My Day, Important, Planned, All and Completed
• Add steps, due dates, reminders, importance and notes
• Group task lists and give them colors
• Sync with Microsoft To Do, Google Tasks, Exchange and CalDAV

AT A GLANCE
• Add Wino widgets to the Windows 11 Widgets Board, or pin the Live Tile on Windows 10
• Get instant push notifications and act on a message from its notification
• Check your next meeting, unread mail and My Day tasks from the system tray

PRIVACY & SECURITY
• Block email trackers and remote content
• Set rules for the senders and domains you trust
• Clean tracking from links and get a warning before risky attachments open
• Back up and restore your data with password protection

MAKE IT YOURS
• Pick a theme from the gallery or create your own from a wallpaper and accent color
• Use Light, Dark and Windows contrast themes, and a colorful icon style
• Show only the modes you use and give each account its own picture

WINO INTELLIGENCE (OPTIONAL)
• Wino Assistant: press a shortcut in any app, then type or say what you want done. Wino shows its plan and changes nothing until you say Yes
• Daily briefing: the mail that matters, with actions like Pay invoice or Track shipment
• Summarize and translate any message
Wino Intelligence needs a Wino Account subscription. Everything else in this list is free.

Wino Mail is for people who want a dependable, modern replacement for the old Windows Mail and Calendar apps. It is open source: https://github.com/bkaankose/Wino-Mail

## Product features (20 at most, 200 characters each)

1. Outlook, Gmail, iCloud, Exchange Server, IMAP and POP3 accounts in one app
2. Native Windows app for x64 and ARM64, not a web wrapper
3. Four modes in one window: Mail, Calendar, People and To Do
4. Account groups that combine several accounts into one inbox
5. Hover actions, swipe actions and searchable context menus
6. Mail filters that move, archive, flag or mark new mail for you
7. Event invitations you can accept or decline from the message
8. Outlook, Google, Exchange and CalDAV calendars in one view
9. Contacts with address books, lists, categories and favorites
10. Tasks with My Day, steps, due dates and reminders
11. Sync with Microsoft To Do, Google Tasks and CalDAV task lists
12. Windows 11 widgets and Windows 10 Live Tiles
13. Instant push notifications with actions
14. Tracker blocking, remote content rules and clean links
15. Theme gallery, custom themes and a colorful icon style
16. Light, Dark and Windows contrast themes
17. Dark mode for messages
18. Password-protected backup and restore
19. Wino Assistant, daily briefing, summaries and translation (subscription)
20. Open source

## Search terms (7 at most, 21 words in total)

mail client, email app, outlook gmail imap, calendar caldav, exchange mail, contacts and tasks, windows mail replacement

## Screenshots

1920x1080 canvases rendered at 2x (3840x2160 PNG). Files are in `out/`; other languages are in `out/<language>/`. The set is light by default, with a dark duplicate of Mail and of Calendar. Every scene has its own colors (`META` in `build.py`) and its icon as a large mark in the backdrop. The caption is the text Partner Center shows with the screenshot. The set has 10 images, the most the Store takes for desktop.

| File | Headline | Layout | Source | Caption |
| --- | --- | --- | --- | --- |
| `01-mail.png` | All your accounts, one calm inbox | full | app capture, light | Outlook, Gmail, iCloud, Exchange, IMAP and POP3 accounts in one fast, native Windows app. |
| `02-mail-dark.png` | (same) | full | app capture, dark | Wino Mail in dark mode. |
| `03-calendar.png` | Your week, right next to your mail | full | app capture, light | Outlook, Google, Exchange and CalDAV calendars in one view, with invitations you answer from the message. |
| `04-calendar-dark.png` | (same) | full | app capture, dark | Wino Calendar in dark mode. |
| `05-people.png` | Everyone you write to, in one place | zoom | app capture, light | Contacts from every account, with address books, lists, categories and favorites. |
| `06-todo.png` | Plan your day beside your inbox | zoom | app capture, light | My Day, steps, due dates and reminders, in sync with Microsoft To Do, Google Tasks and CalDAV. |
| `07-features.png` | Everything you need, built in | top | drawn | Live push notifications, grouped accounts, aliases, templates, signatures, spellchecker, shortcuts, widgets and Live Tiles. |
| `08-companion.png` | Your day at a glance | side | app capture, drawn taskbar | Check your next meeting, unread mail and My Day tasks from the system tray, without opening Wino. |
| `09-themes.png` | Make it yours | zoom | app capture, light | Pick a theme from the gallery or build one from your own wallpaper and accent color. |
| `10-assistant.png` | Wino Assistant | side | drawn voice bubble and taskbar | Wino Assistant is a privacy-first smart planner for your mail, calendar and tasks. Paid subscription. |

Layouts: `full` is a one-line headline above the whole app window; `zoom` is a one-line headline above an enlarged capture that runs off the bottom; `top` is a headline above a drawn panel; `side` is a headline beside a flyout over a drawn Windows 11 taskbar.

## Promotional art

`python scripts/release/store-listing/build.py --art` renders three images into `out/art/`, in the Mail scene's colors:

| File | Size | Store field |
| --- | --- | --- |
| `hero-16x9.png` | 3840x2160 | 16:9 super hero art; no text, logo right of center so the Store's title overlay does not cover it |
| `box-1x1.png` | 2160x2160 | 1:1 box art; logo and the name "Wino Mail" |
| `poster-2x3.png` | 1440x2160 | 2:3 poster art; logo, name and the Calendar, People and To Do logos |

The sizes are the larger of the two the Store accepted for each field when this was written; confirm them in Partner Center before uploading. None of the three uses the all-modes icon.

## Captures

A scene uses a capture from `screens/` when the file exists and draws a placeholder when it does not. Light captures end in `-light.png`; dark ones have no suffix. Run `python scripts/release/store-listing/build.py` after changing one (it needs Python with Pillow, and Edge).

- `mail`, `calendar`: the whole Wino window at 1991x1199, in light and in dark. Mail shows the Application folder with the "Adjustments to the Taiwan Storefront" message selected; the dark capture uses the reader's dark renderer.
- `people`, `todo`, `themes`: the window at 1300x900, light, shown enlarged with the navigation pane in view.
- `companion`: the Companion flyout alone, light. Its global shortcut is off by default; switch it on to open the flyout and off again afterwards.

The captures of 17 Nov 2026 come from the Store build 2.2.13 with the Default theme. Mail shows the maintainer's real Application folder. Calendar, People, To Do and Companion show made-up data that was written into a copy of the app database for the session; the real database was restored afterwards. The scripts (`demo_db.py backup | build | restore`, `capture_v3.sh`) and the database backups are in `~/.wino-lab/app-backups/store-listing-20261117/`.

The features image and the Assistant's voice bubble are drawn; their text is in `i18n/`.

## Languages

`i18n/<language>.json` holds the scene text and the listing text of one Store language. `en-us.json` is generated from this file and `release-notes/.out/<version>/store.txt`; `de-de`, `es-es`, `fr-fr`, `it-it` and `pt-br` are translations of it. `python scripts/release/store-listing/build.py --lang all` renders every language into `out/<language>/`. Headlines, the features image and the Assistant bubble are translated; the app captures show the English app in every language.

## Store submission

Submission `1152921505702428310` was committed through the Store submission API on 17 Nov 2026 with manual publish. It holds the package `WinoMail_Store_2.2.13.0.msixupload` (x86, x64, ARM64) in place of the 2.2.11.0 bundle, and listings for en-us, de-de, es-es, fr-fr, it-it and pt-br: description, short description, 20 features, search terms, the 2.2.13 "What's new" text, the 10 screenshots and the hero, box and poster art.

Changes staged through the API do not appear in Partner Center until the submission is committed, and committing submits it for certification. A staged draft is committed with `store-api.ps1 -Path "applications/9NCRCVJC50WL/submissions/<id>/commit" -Method Post`. An earlier submission made the same way was withdrawn while in certification. The scripts (`store-api.ps1`, `assemble_submission.py`) are in `~/.wino-lab/app-backups/store-listing-20261117/`.
