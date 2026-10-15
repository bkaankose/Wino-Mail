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
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels.Diagnostics;

/// <summary>
/// Debug-only demo account for simulators and emulators, where no real account can be added (the iOS bridge command
/// <c>seedmail</c>, the Android debug command activity): a local IMAP account "Wino Demo" with a closed loopback port and
/// placeholder test credentials (synchronization fails fast and nothing leaves the device), its root alias (the draft
/// sender), eight folders (Projects/2026 under More) and 28 mails with threads, flags, unread and attachment states across
/// several days. The caller opens Mail afterwards.
/// </summary>
public static class MailDebugSeed
{
    private const string DemoAddress = "demo@wino.test";

    public static async Task<string> SeedAsync(IServiceProvider services)
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
            Name = "Wino Demo",
            SenderName = "Wino Demo",
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
            DisplayName = "Wino Demo",
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
            Folder(account, "INBOX", "Inbox", SpecialFolderType.Inbox, 0),
            Folder(account, "Drafts", "Drafts", SpecialFolderType.Draft, 1),
            Folder(account, "Sent", "Sent", SpecialFolderType.Sent, 2),
            Folder(account, "Archive", "Archive", SpecialFolderType.Archive, 3),
            Folder(account, "Junk", "Junk", SpecialFolderType.Junk, 4),
            Folder(account, "Trash", "Trash", SpecialFolderType.Deleted, 5),
            Folder(account, "Projects", "Projects", SpecialFolderType.Other, 6),
            Folder(account, "Projects/2026", "2026", SpecialFolderType.Other, 7, "Projects")
        };
        foreach (var folder in folders) await folderService.InsertFolderAsync(folder);

        var mail = services.GetRequiredService<IMailService>();
        var now = DateTime.Now;
        var senders = new (string Name, string Address)[]
        {
            ("Ada Lovelace", "ada@example.com"), ("Grace Hopper", "grace@example.com"), ("Alan Turing", "alan@example.com"),
            ("Katherine Johnson", "katherine@example.com"), ("Linus Torvalds", "linus@example.com"), ("Margaret Hamilton", "margaret@example.com")
        };
        var subjects = new[]
        {
            "Quarterly planning notes", "Lunch on Thursday?", "Build is green again", "Design review: Mailboxes", "Invoice 2026-114",
            "Trip itinerary", "Re: Release checklist", "Weekend hike photos", "Security update available", "Welcome to the team"
        };
        int created = 0;
        for (int index = 0; index < 24; index++)
        {
            var sender = senders[index % senders.Length];
            var date = now.AddHours(-(index * 7 + 1));
            var thread = index is 1 or 4 or 9 ? "thread-planning" : $"thread-{index}";
            var subject = thread == "thread-planning" ? "Quarterly planning notes" : subjects[index % subjects.Length];
            var package = Mail(account, "INBOX", sender, subject, date, thread, isRead: index % 3 != 0, isFlagged: index % 5 == 0, hasAttachments: index % 4 == 1, focused: index % 6 != 5);
            if (await mail.CreateMailAsync(account.Id, package)) created++;
        }
        for (int index = 0; index < 4; index++)
        {
            var sender = senders[(index + 2) % senders.Length];
            if (await mail.CreateMailAsync(account.Id, Mail(account, "Archive", sender, subjects[(index + 3) % subjects.Length], now.AddDays(-(index + 3)), $"archive-{index}", true, false, false, true))) created++;
        }
        return $"seeded account {account.Id} with {created} mails";
    }

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

    private static NewMailItemPackage Mail(MailAccount account, string folder, (string Name, string Address) sender, string subject, DateTime date,
        string thread, bool isRead, bool isFlagged, bool hasAttachments, bool focused)
    {
        var messageId = $"{Guid.NewGuid():N}@wino.test";
        var preview = $"Hi, a short note about {subject.ToLowerInvariant()}. This message was created by the debug seed.";
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(sender.Name, sender.Address));
        mime.To.Add(new MailboxAddress(account.SenderName, account.Address));
        mime.Subject = subject;
        mime.Date = new DateTimeOffset(date);
        mime.MessageId = messageId;
        mime.Body = new TextPart("html") { Text = $"<p>{preview}</p><p>Regards,<br>{sender.Name}</p>" };
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
