# 🎉 Welcome to Wino Mail – What's New?

Thank you for using Wino Mail! This update is one of the biggest yet, bringing a brand-new Wino Calendar, major security improvements, and a ton of quality-of-life upgrades. Here's a tour of everything new:

---

## 📅 Wino Calendar

Wino now ships with a fully integrated calendar alongside your mail. You can view, create, and manage your events without ever leaving the app. If you use any CalDAV-compatible service (like iCloud, Fastmail, or a self-hosted server), your events will sync automatically and stay up to date. Recurring events, reminders, RSVP responses, and online meeting links are all supported. When someone sends you a calendar invitation by email, Wino will recognize it and let you accept or decline right from the mail reading view.

- View, create, edit, and delete calendar events
- Sync with any CalDAV-compatible calendar service
- Full recurring event support
- RSVP directly from invitation emails
- Reminders and "Join Online" links for virtual meetings
- Calendar settings integrated into the main Settings page

---

## 🔒 Email Signing & Encryption (S/MIME)

You can now send digitally signed emails so recipients know a message genuinely came from you, and encrypt outgoing emails so only the intended recipient can read them. When you receive a signed or encrypted email, Wino will verify the signature and decrypt the content automatically. Import your personal certificate once from **Settings → Signature & Encryption**, and Wino takes care of the rest. Each email address (alias) can have its own certificate.

- Import your personal S/MIME certificate (PKCS#12 / .pfx)
- Sign and/or encrypt outgoing emails with toggle buttons in the compose toolbar
- Visual indicator on received emails that are signed or encrypted
- Automatic signature verification and decryption on incoming mail

---

## 💬 Threaded Mail View

Emails that belong to the same conversation are now grouped into threads, making it much easier to follow a back-and-forth discussion without scrolling through your entire inbox. Threads expand and collapse smoothly, and you can select or act on individual messages within a conversation.

---

## 📎 Large Attachments for Outlook

Sending large files via your Outlook or Microsoft 365 account no longer fails. Wino now uses Microsoft's upload session API behind the scenes, which handles big attachments reliably regardless of file size.

---

## 🔔 Smarter Notifications

Toast notifications now let you act on emails directly from the notification (mark as read, delete, etc.) even if the app is not open. Clicking a calendar reminder notification takes you straight to that event. Notifications for mail and calendar are now routed to the correct app entry automatically.

---

## 🗂️ Folder Management

You can now create new sub-folders and delete existing folders directly from the sidebar — no need to go to your webmail to organize your mailbox. A new Storage settings page also lets you see how much space Wino is using on your device.

---

## 💫 Swipe Actions

Swipe left or right on emails in the mail list to quickly archive, delete, or mark them — ideal for touch screen devices or when you want to process your inbox fast.

---

## ⌨️ Keyboard Shortcuts

A new keyboard shortcuts dialog is available so you can discover all the keyboard shortcuts Wino supports. Press the shortcut or find it in the app menu to open it.

---

## 🖨️ Custom Print Dialog

Printing an email now uses Wino's own print dialog, giving you a cleaner and more consistent experience.

---

## 🚀 Faster App Startup

Wino's internals have been modernized to take full advantage of the latest .NET runtime optimizations. While this is a behind-the-scenes change, it means the app starts quicker, uses less memory, and is set up for even better performance in future updates.

---

## 🐛 Bug Fixes & Stability

- Fixed several issues with Outlook sync reliability and speed
- Improved IMAP synchronization to be more stable and resource-efficient
- Fixed duplicate mail and calendar event issues
- Improved account sign-out and re-authentication handling
- Better error messages when something goes wrong during sync
- Dozens of smaller fixes throughout the app
