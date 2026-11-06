#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels.Diagnostics;

/// <summary>
/// Debug-only demo account for simulators and emulators, where no real account can be added (the iOS bridge command
/// <c>seedmail</c>, the Android debug command activity, the Mac bridge): a local IMAP account "Personal" (Alex Morgan) with a closed loopback port and
/// placeholder test credentials (synchronization fails fast and nothing leaves the device), its root alias (the draft
/// sender), eight folders (Projects/2026 under More) and 28 mails with threads, flags, unread and attachment states across
/// several days, written as fictional but realistic mail so the result can be shown in screenshots. It also grants Calendar,
/// Contacts and To Do and fills them through <see cref="CalendarDebugSeed"/> and <see cref="ModeDebugSeed"/>. The caller opens Mail afterwards.
/// </summary>
public static class MailDebugSeed
{
    private const string DemoAddress = "alex@example.com";

    public static async Task<string> SeedAsync(IServiceProvider services, bool includeOtherModes = true)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        if ((await accounts.GetAccountsAsync()).FirstOrDefault(account => account.Address == DemoAddress) is { } existing)
        {
            // Accounts seeded before the root alias was added cannot create drafts (New Message, Reply).
            if (await accounts.GetPrimaryAccountAliasAsync(existing.Id) is null) await accounts.CreateRootAliasAsync(existing.Id, DemoAddress);
            return "already seeded";
        }

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Personal",
            SenderName = "Alex Morgan",
            Address = DemoAddress,
            ProviderType = MailProviderType.IMAP4,
            AccountColorHex = "#0078D4",
            AttentionReason = AccountAttentionReason.None,
            IsMailAccessGranted = true,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths
        };
        // Placeholder test credentials for a closed loopback port: synchronization fails fast and nothing leaves the device.
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            DisplayName = "Personal",
            Address = DemoAddress,
            IncomingServer = "127.0.0.1",
            IncomingServerPort = "1",
            IncomingServerUsername = DemoAddress,
            IncomingServerPassword = "demo",
            OutgoingServer = "127.0.0.1",
            OutgoingServerPort = "1",
            OutgoingServerUsername = DemoAddress,
            OutgoingServerPassword = "demo",
            IncomingServerType = CustomIncomingServerType.IMAP4,
            MaxConcurrentClients = 1
        };
        await accounts.CreateAccountAsync(account, server);
        // Onboarding gives every account its root alias; draft creation (New Message, Reply) needs it as the sender.
        await accounts.CreateRootAliasAsync(account.Id, DemoAddress);

        var folderService = services.GetRequiredService<IFolderService>();
        var folders = new[]
        {
            Folder(account, "INBOX", "Inbox", SpecialFolderType.Inbox, 1),
            Folder(account, "Drafts", "Drafts", SpecialFolderType.Draft, 2),
            Folder(account, "Sent", "Sent", SpecialFolderType.Sent, 3),
            Folder(account, "Archive", "Archive", SpecialFolderType.Archive, 4),
            Folder(account, "Junk", "Junk", SpecialFolderType.Junk, 5),
            Folder(account, "Trash", "Trash", SpecialFolderType.Deleted, 6),
            Folder(account, "Projects", "Projects", SpecialFolderType.Other, 7),
            Folder(account, "Projects/2026", "2026", SpecialFolderType.Other, 8, "Projects")
        };
        foreach (var folder in folders) await folderService.InsertFolderAsync(folder);

        var mail = services.GetRequiredService<IMailService>();
        var now = DateTime.Now;
        // Fictional people and mail, readable enough for screenshots of the app.
        var messages = new (string Name, string Address, string Subject, string Body)[]
        {
            ("Emma Lindqvist", "emma@northwind.example", "Quarterly planning notes",
                "Thanks for the input yesterday. I folded everyone's notes into one plan for the next quarter: the mobile release moves to May, and we keep two weeks free for polish before launch."),
            ("Daniel Okafor", "daniel@contoso.example", "Lunch on Thursday?",
                "The new ramen place opened next to the office. Are you free on Thursday around 12:30? I can book a table for four if Sofia and Lucas want to join."),
            ("Sofia Martins", "sofia@fabrikam.example", "Build is green again",
                "The flaky sync test is fixed and every pipeline passed overnight. I also cut the build time by about four minutes by caching the package restore."),
            ("Lucas Becker", "lucas@adatum.example", "Design review: new sidebar",
                "Here are the updated mockups for the sidebar. Accounts stay at the top, folders follow, and the selected item now uses the accent colour. Feedback welcome before Friday."),
            ("Billing Team", "billing@tailspin.example", "Your invoice for October is ready",
                "Your invoice for October is available. The amount will be charged to your card on file on November 3. You can download a PDF copy from your account page."),
            ("Mia Tanaka", "mia@litware.example", "Trip itinerary for Lisbon",
                "Flights and hotel are confirmed. We land on Friday at 10:40, check in at noon, and the team dinner is at 19:30 near the river. The full itinerary is attached."),
            ("Noah Fischer", "noah@woodgrove.example", "Re: Release checklist",
                "Release notes are drafted and the store screenshots are ready. The only open item is the final translation pass, which should land by tomorrow morning."),
            ("Olivia Brown", "olivia@proseware.example", "Weekend hike photos",
                "What a view from the top! I uploaded all the photos to the shared album. The one with the fog rolling over the valley is my favourite."),
            ("Security", "security@margie.example", "New sign-in to your account",
                "We noticed a new sign-in to your account from a Mac in Berlin. If this was you, there is nothing else to do. If not, please secure your account."),
            ("Hannah Weber", "hannah@contoso.example", "Welcome to the team!",
                "We are so glad you are joining us. Your first week starts with an onboarding session on Monday at 9:00, followed by lunch with the whole team.")
        };
        int created = 0;
        for (int index = 0; index < 24; index++)
        {
            var thread = index is 1 or 4 or 9 ? "thread-planning" : $"thread-{index}";
            var message = thread == "thread-planning" ? messages[0] : messages[index % messages.Length];
            var date = now.AddHours(-(index * 7 + 1));
            var package = Mail(account, "INBOX", message, date, thread, isRead: index % 3 != 0, isFlagged: index % 5 == 0, hasAttachments: index % 4 == 1, focused: index % 6 != 5);
            if (await mail.CreateMailAsync(account.Id, package)) created++;
        }
        for (int index = 0; index < 4; index++)
        {
            var message = messages[(index + 3) % messages.Length];
            if (await mail.CreateMailAsync(account.Id, Mail(account, "Archive", message, now.AddDays(-(index + 3)), $"archive-{index}", true, false, false, true))) created++;
        }
        // The demo account offers every mode, like a real account with calendars, contacts and To Do. The mode seeds
        // that create this account for themselves pass false, so their own data is not added twice.
        if (!includeOtherModes) return $"seeded account {account.Id} with {created} mails";
        var calendar = await CalendarDebugSeed.SeedAsync(services, DemoAddress);
        var contacts = await ModeDebugSeed.SeedContactsAsync(services, DemoAddress);
        var tasks = await ModeDebugSeed.SeedTasksAsync(services, DemoAddress);
        return $"seeded account {account.Id} with {created} mails; {calendar}; {contacts}; {tasks}";
    }

    // Order starts at 1: the folder list sorts 0 as "no order" and puts it last.
    private static MailItemFolder Folder(MailAccount account, string remoteId, string name, SpecialFolderType type, int order, string? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        MailAccountId = account.Id,
        RemoteFolderId = remoteId,
        ParentRemoteFolderId = parent ?? string.Empty,
        FolderName = name,
        SpecialFolderType = type,
        IsSystemFolder = type != SpecialFolderType.Other,
        IsSticky = type != SpecialFolderType.Other,
        IsSynchronizationEnabled = true,
        ShowUnreadCount = true,
        IsCountedInAccountTotal = type == SpecialFolderType.Inbox,
        Order = order
    };

    private static NewMailItemPackage Mail(MailAccount account, string folder, (string Name, string Address, string Subject, string Body) message,
        DateTime date, string thread, bool isRead, bool isFlagged, bool hasAttachments, bool focused)
    {
        var messageId = $"{Guid.NewGuid():N}@wino.test";
        var sender = (message.Name, message.Address);
        var subject = message.Subject;
        var preview = message.Body;
        var firstName = account.SenderName.Split(' ')[0];
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(sender.Name, sender.Address));
        mime.To.Add(new MailboxAddress(account.SenderName, account.Address));
        mime.Subject = subject;
        mime.Date = new DateTimeOffset(date);
        mime.MessageId = messageId;
        mime.Body = new TextPart("html")
        {
            Text = $"<p>Hi {WebUtility.HtmlEncode(firstName)},</p><p>{WebUtility.HtmlEncode(preview)}</p>" +
                "<p>Let me know if anything needs a closer look, and have a great day.</p>" +
                $"<p>Best,<br>{WebUtility.HtmlEncode(sender.Name)}</p>"
        };
        var copy = new MailCopy
        {
            Id = messageId,
            MessageId = messageId,
            ThreadId = thread,
            FromName = sender.Name,
            FromAddress = sender.Address,
            Subject = subject,
            PreviewText = preview,
            CreationDate = date.ToUniversalTime(),
            IsRead = isRead,
            IsFlagged = isFlagged,
            HasAttachments = hasAttachments,
            IsFocused = focused,
            FileId = Guid.NewGuid(),
            Importance = MailImportance.Normal
        };
        return new NewMailItemPackage(copy, mime, folder);
    }
}
#endif
