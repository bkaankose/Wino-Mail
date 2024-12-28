# Contribution Guideline

This project started as a side project of mine but grew something bigger than I expected and people loved it. Therefore, I open sourced it for others to contribute as well to have the best alternative mail client to Mail & Calendars so far.

You can contribute to Wino in multiple ways. It can be a feedback or bug report you open here, join discussions in the Discord channel to shape the way the product goes, create proposals or check for opened and approved bugs to fix them.

Feeling rich? You can always [donate via Paypal](https://www.paypal.com/donate/?hosted_button_id=LGPERGGXFMQ7U)

![Paypal Donate](https://www.winomail.app/images/paypal_donate_qr.png "Paypal Donate")

## Getting Started

Wino Mail is [Universal Windows Platform](https://learn.microsoft.com/en-us/windows/uwp/get-started/universal-application-platform-guide) application. UI is built mostly using [WinUI 2](https://learn.microsoft.com/en-us/windows/apps/winui/winui2/), but the application itself is not a WinUI 3 application.

**Min Version:** Windows 10 1809 
**Target Version:** Windows 11 22H2

## Prerequisites

* ".NET desktop development" workload in Visual Studio 2022+
* .NET SDK 8.0+

With those installed, it's pretty straightforward after cloning the repo.  Just open **Wino.sln** solution in your IDE and launch.

## Project Architecture

Wino Mail supports 3 different types of synchronization depending on the provider type.

- Outlook / Office 365
- Gmail
- IMAP / SMTP

Project use [MimeKit](https://github.com/jstedfast/MimeKit) and [Mailkit](https://github.com/jstedfast/MailKit/) libraries extensively to perform most of it's functionalities. Specially for IMAP synchronizer. Other synchronizers built on [Microsoft Graph SDK](https://github.com/microsoftgraph/msgraph-sdk-dotnet) for Outlook/Office 365 and [Gmail API Client Library](https://developers.google.com/api-client-library/dotnet/apis/gmail/v1) for Gmail.

Authentication is handled by **Authenticators**, except for IMAP. Server info and credential details are stored in **CustomServerInformation** table in the database. For API synchronizers, check out **GmailAuthenticator** and **OutlookAuthenticator**

Each action you take on mails (like mark as read, delete, move etc.) are delegated as a request to **WinoRequestDelegator** and **WinoRequestProcessor** respectively. These services will do preliminary checks for those requests, batch them together to reduce the network calls made to APIs or IMAP server, queue them to corresponding synchronizer for the given account and optionally send a request to synchronizer to run them in batches. Requests are batched by the logic in **RequestComparer**.

### Solution Overview
![Solution Overview](https://www.winomail.app/images/sln_overview.png "Solution Overview")

**Wino.BackgroundTasks**: Project that contains UWP background tasks that Wino Mail use for various reasons like background synchronization or application updated messages.

**Wino.Core**: .NET Standard 2.0 library that does all the work for synchronization, authentication and local database management. All services that don't need UWP reference can go here.

**Wino.Core.Domain**: .NET Standard 2.0 library that contains the models, interfaces and translations.

**Wino.Core.UWP**: All shared services that executes code that requires WinRT APIs. Right now only Wino Mail use these services, but once we start doing **Wino Calendar** it'll be used by that project as well.

**Wino.Mail**: Actual UWP application. This project holds the UI elements, styles, assets etc. You must launch this project to start debugging Wino Mail.

**Wino.Mail.ViewModels**: Contains the view models for pages, all models that require INotifyPropertyChanged and messages for pub-sub.

### Good to know

- Application has local SQLite database, stored in [publisher's cache folder](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-publishercachefolders). This is because I want to use the same database or local path for the database for Wino Calendar later on. You can get the path to database for yourself by debugging **DatabaseService**.
- Project tries to follow MVVM pattern as much as possible. [MVVM Toolkit](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) library is used for following the best MVVM approach.
- Project has event Pub-Sub on top of MVVM and it's widely used with [Messenger](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/messenger)
- Downloaded EML files for mails are stored in application's local storage, not in publisher cache folder.
- Database does not store full mails, but only the minimum amount of data extracted from the original MIME content for app to work with. Each mail will have **FileId** in the database (**MailCopy** table) to resolve the EML file for MIME content on the disk. Paths are resolved in **MimeFileService**.
- As a rule, I want to avoid introducing new libraries into the code as much as I can. Try to avoid it as long as you really really don't need it. This will help maintainability going forward.
- Project has custom localization system built in to support changing the language at runtime. If you want to change or add new translation string, check out **resources.json** file under **Wino.Core.Domain\Translations\en-US** path. This is the **only** file you must change. Do not touch other resources.json file that belongs to other languages. Crowdin will do the management of those files after your changes automatically. After changing the file, locate **Translator.tt** [T4 Template](https://learn.microsoft.com/en-us/visualstudio/modeling/code-generation-and-t4-text-templates?view=vs-2022) under **Wino.Core.Domain** project, right click and click **"Run Custom Tool"**. This template will generate **Translator.Designer.cs** file with your new translations. To access it in the code, reference **Translator** object in the code, or bind to it in XAML.
- Cached user settings (like which folder to expand on launch) are managed in **PreferencesService**.
- Cached UI values at runtime (like whether the reader is opened or whether the navigation menu is opened at the moment) are managed in **StatePersistenceService**.
- Rendering mails are done with [WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui2). Wino has built-in custom [Quill](https://github.com/quilljs/quill) editor for composing and rendering mails. **Wino.Mail project has JS folder** for that purpose. **reader.html** is for reading mails, **full.html** is for composing mails. This editor is slightly customized to provide better visual experience to the users but doesn't have all the functionality that Quill offers for now.
- x86, x64 and ARM32 is supported for now. I have ongoing efforts to make it compile for ARM64 as well, but I need more time to complete it.

## How to work on
### New Issues

**Please create an issue here first and say that you would like to work on it**. I'll have it assigned to you after confirming the bug.

### Existing Issues

**Please comment under the issue** and I'll have it assigned to you. This will prevent all of us to save big time.

### New Implementations and Big Things

If you'd like to work on something big and implement a huge new system into the code, **please create a proposal first**. We can collectively discuss over the proposal, gather more feedback to improve it or just accept it as it is. 

**Please keep in mind that not all of the proposals will go to Wino.** Project's first goal is to create the same experience as Windows Mail & Calendars. At some point if your proposal will go against the motto your proposal might be rejected for implementation. Keep in mind that we are not trying to become the next Outlook or other major fully featured mail clients here (yet). Therefore, it's important to start working on it as soon as the proposal is approved, not before. I appreciate your understanding on this matter.

## Additional Help

Project does not have a separate Discord server, but has 2 different dedicated channels under 2 different servers that I actively monitor every day.

**[UWP Community](https://discord.gg/wNMGxYZMFy)** under Apps & Projects -> **wino-mail**

**[Developer Sanctuary](https://discord.gg/windows-apps-hub-714581497222398064)** under Community Projects -> **wino-mail**

You can always send an e-mail to bkaankose (at) outlook.com for extras.




