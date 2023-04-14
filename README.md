![pdark](https://user-images.githubusercontent.com/12009960/232114528-2d2c8e3c-dbe7-429a-94e0-6aecc73bdf70.png)

[Download Latest Version from Microsoft Store (Preview 1.3)](https://www.microsoft.com/store/apps/9NCRCVJC50WL)

## 📧 Wino Mail
Wino is a lightweight mail client for Windows 10/11 that supports Gmail and Outlook accounts. It is still in active development. Main motivation for this project is to replace Mail & Calendar for Windows with modern Windows design.

## 💡 What's new in Preview 1.3?

- **Basic Mail Composing** has arrived! In Preview 1.2, Wino did not support sending new mails or replying to existing mails. In this version Wino has a simplified version of [Quill Editor](https://quilljs.com/) for start. You can create new draft mails, reply or reply all to existing mails. There are still some limitations and don't forget that this is the first WYSIWYG editor for Wino. I hope to improve this later on and support for creating more complex mails later on. You can find what is not working in Missing Parts section of this document.
- **New composer page** supports creating MIME messages with the editor mentioned above. You will also get notified when the draft you are working on actively is updated.
- **Drafts** are implemented now and they should be working. Wino will only synchronize drafts **one way**, meaning that your draft in Wino will **not** be synchronized until you send it. Please read 'Missing Parts' section for details.
- [**Dark Reader**](https://github.com/darkreader/darkreader) implementation for reading! New compose page and existing mail rendering page will support displaying e-mails in light or dark mode as preferred. Your light or dark theme is respected, but if you encounter issues with reading you can manually toggle to invert theme.
- **Instant changes on the UI**. Wino already batches your network requests to save API quota and bandwith, but your changes were not reflected to UI immidiately in Preview 1.2 Now all the changes you do will tried to be reflected to UI immidiately without waiting for a network result.
- Some synchronizer improvements that will make sure your mails are synced properly through APIs. Wino does not use classic IMAP/SMPTP approach to synchronize mails, but it does provider APIs and SDKs to do these. With the recent changes, mail synchronization should feel more seamless.
- **New icons** for UI. Design has still inconsistencies, but this version feels more consistent compared to Preview 1.2. Most of the icons are replaced from Microsoft's Fluent Icon pack. Design and icons are likely to change to increase consistency later on though.
- **Pre-Launch Activation** is enabled. This means that if Windows allows Wino to be launched silently in background on system startup, when you launch Wino everything should load instantly. This is likely to be tested better and really depends on how Windows behaves, but it is there. If it works for you I consider this as an improvement :)
- **Support for on the fly MIME fetch**. With this, you won't receive lots of 'Cant find MIME message' anymore.
- **Fixed**: [Weird artifact on the rightmost context menu](https://github.com/bkaankose/Wino-Mail/issues/18)
- **Fixed**: [Cannot see the addressees and people in cc for received mails](https://github.com/bkaankose/Wino-Mail/issues/17)
- **Fixed**: [E-mails rendered in light mode even though the app is in dark mode](https://github.com/bkaankose/Wino-Mail/issues/13)
- **Fixed**: [Threads do not work](https://github.com/bkaankose/Wino-Mail/issues/15)
- **Fixed**: [Apostrophes are not rendered properly in the e-mails list](https://github.com/bkaankose/Wino-Mail/issues/10)
- **Fixed**: [Per-mail context menus elements are not rendered properly with touch screen](https://github.com/bkaankose/Wino-Mail/issues/9)

There are couple more fixes and improvements for mail listing as well.

## 🚫 Missing Parts in Preview 1.3

Creating fully functional mail client is hard. Just like in preview 1.2, this version also has some missing bits. . **Here are some of the changes that are not working in this release:**.

- **Search is still disabled**. Will be implemented later on with online search functionality.
- **Forwarding and Moving Mails** are not implemented, just like in Preview 1.2.
- **Drafts are synchronized one way only (API -> Wino)** This means when you create a new draft in Wino or reply to existing mail, any changes you make in Wino will not be synchronized to Gmail or Outlook. However, if you do some changes in Gmail or Outlook, **Wino will get those changes.** Your local draft is automatically updated everytime you close the composer though.
- **Replying to mails will not include previous mail in the reply.** For this to properly work, Wino must know about your account settings in Gmail or Outlook, which right now is not synchronized during synchronization. For now, all drafts are created with the default signature, and replying mail's content is not included in the reply. This feature will be improved later on.

## 🙌 Big Thank You

I apprecite all the people who gave Wino a try and provided feedback! If you encounter any issue or would like to provide feedback for Wino, please visit the [GitHub page](https://github.com/bkaankose/Wino-Mail/) and open an issue.
