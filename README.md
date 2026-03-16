<p align="center">
  <a href="https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full">
    <img src="https://www.winomail.app/images/v2/Logo.png" width="90" height="90" alt="Wino Mail logo">
  </a>

  <h3 align="center">Wino Mail</h3>

  <p align="center">
    Native mail and calendar client for Windows.
  </p>
</p>

<br>

![Wino Mail screenshot](https://user-images.githubusercontent.com/12009960/232114528-2d2c8e3c-dbe7-429a-94e0-6aecc73bdf70.png)

## Motivation

I'm a big fan of Windows Mail & Calendars due to its simplicity. Personally, I find it more intuitive for daily use cases compared to Outlook desktop and the new WebView2 powered Outlook version. Seeing [Microsoft deprecating it](https://support.microsoft.com/en-us/office/outlook-for-windows-the-future-of-mail-calendar-and-people-on-windows-11-715fc27c-e0f4-4652-9174-47faa751b199#:~:text=The%20Mail%20and%20Calendar%20applications,will%20no%20longer%20be%20supported.) dragged me into starting to work on Wino a couple of years ago. Wino's main motivation is to bring all the existing functionality from Mail & Calendars over time without changing the user experience that millions have loved since the Windows 8 days in Mail & Calendars.

## vNext Release Highlights

Wino vNext focuses on making Mail, Calendar, and Contacts feel like one cohesive native Windows experience while improving sync reliability and startup responsiveness.

- 📅 **Calendar management:** Event compose/create flow, calendar-mail mapping, reminder snooze support, occurrence and detail-page improvements, and CalDAV correctness fixes.
- 👥 **Contact management:** Improved contact workflows, account/settings integration, and contact data-model cleanup.
- 🔄 **Synchronization reliability:** Refactored synchronizers, better state handling, 404 + 429 error handling, and duplicate-operation prevention.
- ✉️ **Compose and drafts:** Refined editor/toolbar architecture, better rendering pipeline, Gmail draft support, and large Outlook attachment upload sessions.
- ⚡ **Performance and quality:** Faster mail fetching with batched DB queries and caching, SQLite indexing/foreign key enforcement, and broader test + CI coverage.
- 🎨 **WinUI polish:** Improved onboarding/startup, settings and dialogs refresh, notification routing fixes, and keyboard/navigation quality-of-life improvements.

## Features

- 📨 Outlook and Gmail API integration
- 🌐 IMAP/SMTP support for custom mail servers
- 📅 Calendar support with event creation/compose and reminders
- 👥 Contact management and people-centric account experience
- ✅ Core mail actions: send, receive, read/unread, move, spam, and more
- 🔗 Linked/Merged accounts
- 🔔 Toast notifications with background sync
- ⚡ Instant startup-oriented architecture
- 🔎 Offline-capable workflows and search improvements
- 🎛️ Modern responsive WinUI interface with personalization options
- 🌗 Dark/Light mode for mail reader and app surfaces

## Download

Download latest version of Wino Mail from Microsoft Store for free.

<a href="https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Get Wino Mail from Microsoft Store"/>
</a>

## Beta Releases

Stable releases will always be distributed on Microsoft Store. However, beta releases will be distributed in [GitHub Releases](https://github.com/bkaankose/Wino-Mail/releases). Please keep in mind that beta releases might not be for daily use, only for testing purposes and recommended for experienced users or developers. Beta releases are also managed manually. Therefore, code in the repository might be ahead of the released Beta version at the moment. Make sure to compare versions before tryout out the Beta version.

These releases are distributed as side-loaded packages. To install them, download the **.msixbundle** file in GitHub releases and [follow the steps explained here.](https://learn.microsoft.com/en-us/windows/application-management/sideload-apps-in-windows)

## Contributing

Check out the [contribution guidelines](/CONTRIBUTING.md) before diving into the source code or opening an issue. There are multiple ways to contribute and all of them are explained in detail there.

## GitHub Packages Setup

`Wino-Mail` restores `Wino.Mail.Contracts` from the GitHub Packages feed for the `bkaankose` account. The repository-level [`nuget.config`](./nuget.config) already declares this source:

- Source URL: `https://nuget.pkg.github.com/bkaankose/index.json`
- Package mapped to that source: `Wino.Mail.Contracts`

To restore packages locally, configure credentials for that feed in your user-level NuGet config.

### 1. Create a personal access token

Create a classic GitHub personal access token with at least:

- `read:packages` for restoring packages

If you also need to publish packages from your machine, add:

- `write:packages`

### 2. Add the GitHub Packages source to your local NuGet config

Run:

```powershell
dotnet nuget add source https://nuget.pkg.github.com/bkaankose/index.json `
  --name github `
  --username YOUR_GITHUB_USERNAME `
  --password YOUR_GITHUB_PAT `
  --store-password-in-clear-text
```

If you already have a `github` source configured, update it instead:

```powershell
dotnet nuget update source github `
  --source https://nuget.pkg.github.com/bkaankose/index.json `
  --username YOUR_GITHUB_USERNAME `
  --password YOUR_GITHUB_PAT `
  --store-password-in-clear-text
```

### 3. Verify restore

After adding the source, restore as usual:

```powershell
dotnet restore Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

If restore still fails for `Wino.Mail.Contracts`, double-check that:

- your PAT includes `read:packages`
- the source name is `github`
- the source URL exactly matches `https://nuget.pkg.github.com/bkaankose/index.json`

## Donate

Your donations will motivate me more to work on Wino in my spare time and cover the expenses to keep [project's website](https://www.winomail.app/) alive.

- You can [donate via Paypal by clicking here](https://www.paypal.com/donate/?hosted_button_id=LGPERGGXFMQ7U)
- You can buy Unlimited Accounts add-on in the application. It's a one-time payment for lifetime, not a monthly recurring payment.

