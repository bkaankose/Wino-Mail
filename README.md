<p align="center">
  <a href="https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full">
    <img src="https://www.winomail.app/images/v2/Logo.png" width="90" height="90" alt="Wino Mail logo">
  </a>
</p>

<h1 align="center">Wino Mail</h1>

<p align="center">A native Mail, Calendar, People, and To Do client for Windows.</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full">Microsoft Store</a>
  · <a href="https://github.com/bkaankose/Wino-Mail/releases">Beta releases</a>
  · <a href="CONTRIBUTING.md">Contributing</a>
</p>

![Wino Mail screenshot](https://user-images.githubusercontent.com/12009960/232114528-2d2c8e3c-dbe7-429a-94e0-6aecc73bdf70.png)

## Motivation

I'm a big fan of Windows Mail & Calendars due to its simplicity. Personally, I find it more intuitive for daily use cases compared to Outlook desktop and the new WebView2 powered Outlook version. Seeing [Microsoft deprecating it](https://support.microsoft.com/en-us/office/outlook-for-windows-the-future-of-mail-calendar-and-people-on-windows-11-715fc27c-e0f4-4652-9174-47faa751b199#:~:text=The%20Mail%20and%20Calendar%20applications,will%20no%20longer%20be%20supported.) dragged me into starting to work on Wino a couple of years ago. Wino's main motivation is to bring all the existing functionality from Mail & Calendars over time without changing the user experience that millions have loved since the Windows 8 days in Mail & Calendars.

Wino started before the current AI era. The project welcomes AI-assisted contributions when they comply with its coding rules, architecture, and maintainer decisions.

## Download

Choose one distribution channel for Wino Mail. Install either the Microsoft Store version or the signed sideloaded version.

| Distribution | Recommended for | Installation |
| --- | --- | --- |
| Microsoft Store | Most users | [Install Wino Mail from Microsoft Store](https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full) |
| Signed sideloaded package | Users who want direct distribution outside Microsoft Store | [Install Wino Mail with App Installer](https://download.winomail.app/wino.appinstaller) |

The developer signs the sideloaded package. The `.appinstaller` file gives Windows the package location and update information.

<a href="https://apps.microsoft.com/detail/Wino%20Mail/9NCRCVJC50WL?launch=true&mode=full">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Get Wino Mail from Microsoft Store">
</a>

### Beta releases

Beta releases are available from [GitHub Releases](https://github.com/bkaankose/Wino-Mail/releases). They can contain unfinished changes and are for testing.

Beta and other sideloaded packages do not use Microsoft Store distribution. The project signs these packages with Azure Artifact Signing before publication.

Each beta download contains the signed `.msixbundle` and its public certificate. Compare the package version with the repository version before installation.

Follow the [Microsoft sideloading guide](https://learn.microsoft.com/en-us/windows/application-management/sideload-apps-in-windows) if Windows cannot open the App Installer link.

## About Wino

Wino is an open-source replacement for the retired Windows Mail and Calendar applications. It keeps their direct, native Windows experience.

The active desktop application uses WinUI 3. Mail, Calendar, People, and To Do share one account and service architecture.

## Other features

- Native WinUI 3 interface with light, dark, and high-contrast support
- Outlook, Gmail, IMAP, POP3, CalDAV, CardDAV, and local data support
- Linked and merged accounts
- Offline workflows and local search
- Background synchronization and toast notifications
- WebView2-based mail reading and composition
- EML and ICS file integration
- `mailto`, `webcal`, `webcals`, and Wino protocol activation

## Developer setup

Read the [contribution guide](CONTRIBUTING.md) for requirements, build commands, project details, and contribution rules.

## Contributing

Read the [contribution guide](CONTRIBUTING.md) before you start work or open a pull request. Discuss large features in an issue or proposal first.

Contributors remain responsible for every submitted change, including AI-assisted changes.

## Donate

Donations help to fund the project website and development costs.

- [Donate with PayPal](https://www.paypal.com/donate/?hosted_button_id=LGPERGGXFMQ7U)
- Buy the Unlimited Accounts add-on in Wino. It is a one-time purchase.

## License

See [`LICENSE.md`](LICENSE.md).
