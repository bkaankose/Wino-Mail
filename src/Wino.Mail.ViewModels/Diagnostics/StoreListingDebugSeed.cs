#if DEBUG
#nullable enable
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Mail.ViewModels.Diagnostics;

/// <summary>
/// Debug-only demo data for store screenshots, meant for an empty data folder: an Outlook, a Gmail and an iCloud account
/// (so the account list shows each provider's logo), fictional mail in each, one calm week of events across their
/// calendars, a local address book and local task lists. The Outlook and Gmail accounts have no tokens and the iCloud
/// account points at a closed loopback port, so nothing is sent anywhere.
/// </summary>
public static class StoreListingDebugSeed
{
    private const string OutlookAddress = "alex.morgan@outlook.com";

    private sealed record DemoMail(string Name, string Address, string Subject, string Preview, string[] Paragraphs, double HoursAgo,
        bool IsRead = true, bool IsFlagged = false, string? Attachment = null);

    public static async Task<string> SeedAsync(IServiceProvider services)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        if ((await accounts.GetAccountsAsync()).Any(account => account.Address == OutlookAddress)) return "already seeded";

        var outlook = await CreateAccountAsync(accounts, "Personal", OutlookAddress, MailProviderType.Outlook, SpecialImapProvider.None, "#0078D4", 0);
        var gmail = await CreateAccountAsync(accounts, "Side project", "alex.morgan.studio@gmail.com", MailProviderType.Gmail, SpecialImapProvider.None, "#EA4335", 1);
        var icloud = await CreateAccountAsync(accounts, "Family", "alex.morgan@icloud.com", MailProviderType.IMAP4, SpecialImapProvider.iCloud, "#8E8E93", 2);

        int mails = 0;
        mails += await SeedMailAsync(services, outlook,
        [
            new("Avery Chen", "avery.chen@contoso.example", "Project kickoff: agenda and next steps",
                "Hi all, here is the agenda for Thursday. Please have a look at the draft plan before we meet.",
                ["Hi all,", "Thanks for making time this week. Here is the agenda for Thursday, so we can use the hour well and leave with clear owners.",
                 "1. Goals for the first milestone<br>2. Design review of the new onboarding<br>3. Timeline, risks and who owns what",
                 "I attached the draft plan. Have a look before the meeting and add your comments directly in the document.", "Talk soon,<br>Avery"],
                1.5, Attachment: "Kickoff plan.pdf"),
            new("Northwind Travel", "bookings@northwind.example", "Your booking is confirmed",
                "Lisbon, 4 nights. Check-in opens 24 hours before departure.",
                ["Hello Alex,", "Your trip to Lisbon is confirmed: 4 nights at the Rio Hotel, from Friday 27 November.",
                 "Online check-in for your flight opens 24 hours before departure. Your e-tickets are attached.", "Have a great trip,<br>Northwind Travel"],
                2.2, IsRead: false, Attachment: "E-tickets.pdf"),
            new("Jordan Rivera", "jordan.rivera@contoso.example", "Re: Design review notes",
                "Thanks for the quick turnaround. Two small things on the settings page before we ship.",
                ["Hi Alex,", "Thanks for the quick turnaround. Two small things on the settings page: the section headers need a bit more space above them, and the toggle labels should be sentence case.",
                 "Everything else looks great. Let's ship it on Monday.", "Jordan"],
                3.1, IsFlagged: true),
            new("Morgan Patel", "morgan.patel@woodgrove.example", "Lunch on Friday?",
                "There is a new place near the office. 12:30 works for me.",
                ["Hey,", "There is a new ramen place near the office. Friday at 12:30 works for me. Should I book a table for three?", "Morgan"],
                22, IsRead: false),
            new("Contoso Bank", "statements@contoso-bank.example", "Your November statement is ready",
                "Your statement for the period ending 15 November is now available.",
                ["Dear Alex Morgan,", "Your statement for the period ending 15 November is now available in online banking.", "Contoso Bank"],
                26),
            new("Fabrikam Tools", "orders@fabrikam.example", "Your order has shipped",
                "Order 48213 is on its way. Track your package for delivery updates.",
                ["Good news!", "Order 48213 is on its way and should arrive on Tuesday. You can track your package for delivery updates.", "Fabrikam Tools"],
                30),
            new("Sam Kim", "sam.kim@outlook.com", "Photos from the weekend",
                "Finally sorted them. The one from the lookout is my favorite.",
                ["Hi Alex,", "Finally sorted the photos from the hike. The one from the lookout is my favorite. The rest are in the shared album.", "Sam"],
                76, Attachment: "Lookout.jpg"),
            new("Riley Santos", "riley.santos@contoso.example", "Quarterly numbers for review",
                "The figures for Q4 are in. Could you check the forecast tab before Wednesday?",
                ["Hi Alex,", "The figures for Q4 are in. Could you check the forecast tab before Wednesday? I highlighted the cells that changed.", "Thanks,<br>Riley"],
                98, IsFlagged: true, Attachment: "Q4 forecast.xlsx"),
            new("Woodgrove University", "alumni@woodgrove.example", "Alumni newsletter: November",
                "Events near you, a new mentoring program and campus news.",
                ["Dear alumni,", "This month: events near you, a new mentoring program and news from campus.", "Woodgrove University"],
                120),
            new("Mika Okafor", "mika@tailspin.example", "Welcome to the design guild",
                "We meet every second Tuesday. The next session is about color systems.",
                ["Hi Alex,", "Welcome aboard! We meet every second Tuesday. The next session is about color systems, and you are very welcome to bring work in progress.", "Mika"],
                150)
        ]);
        mails += await SeedMailAsync(services, gmail,
        [
            new("Lucas Becker", "lucas@adatum.example", "Logo concepts, round two",
                "Three new directions based on your notes. I like the second one most.",
                ["Hi Alex,", "Here are three new directions based on your notes. I like the second one most; it works well at small sizes.", "Lucas"],
                4, IsRead: false, Attachment: "Logo concepts.pdf"),
            new("Itch Market", "hello@itchmarket.example", "You made a sale!",
                "Someone bought Pocket Planner. Your payout is scheduled for Monday.",
                ["Congratulations!", "Someone just bought Pocket Planner. Your payout is scheduled for Monday.", "The Itch Market team"],
                27),
            new("Emma Lindqvist", "emma@northwind.example", "Beta feedback",
                "I have been using the beta all week. Sync is fast and the widgets are lovely.",
                ["Hi Alex,", "I have been using the beta all week. Sync is fast and the widgets are lovely. One small bug: the dark theme loses the accent color after a restart.", "Emma"],
                50, IsFlagged: true),
            new("Domain Registry", "renewals@registry.example", "Your domain renews in 30 days",
                "pocketplanner.app renews automatically on 21 December.",
                ["Hello,", "pocketplanner.app renews automatically on 21 December. No action is needed.", "Domain Registry"],
                100)
        ]);
        mails += await SeedMailAsync(services, icloud,
        [
            new("Mom", "linda.morgan@icloud.com", "Sunday dinner",
                "Dad is making his lasagna. Bring Sam if she's free!",
                ["Hi sweetheart,", "Dad is making his lasagna on Sunday. Come around 6, and bring Sam if she's free!", "Love,<br>Mom"],
                6, IsRead: false),
            new("Oak Street School", "office@oakstreet.example", "Winter concert on 10 December",
                "The winter concert starts at 18:00 in the main hall.",
                ["Dear parents,", "The winter concert starts at 18:00 in the main hall. Doors open at 17:30.", "Oak Street School"],
                45),
            new("Noah Fischer", "noah.fischer@icloud.com", "Cabin weekend",
                "The cabin is free on the 12th. Should I book it for all of us?",
                ["Hey Alex,", "The cabin is free the weekend of the 12th. Should I book it for all of us?", "Noah"],
                80)
        ]);

        var calendar = await SeedCalendarsAsync(services, outlook, gmail, icloud);
        var contacts = await SeedContactsAsync(services, outlook);
        var tasks = await SeedTasksAsync(services, outlook);
        return $"seeded 3 accounts, {mails} mails, {calendar} events, {contacts} contacts, {tasks} tasks";
    }

    private static async Task<MailAccount> CreateAccountAsync(IAccountService accounts, string name, string address, MailProviderType provider,
        SpecialImapProvider special, string color, int order)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = name,
            SenderName = "Alex Morgan",
            Address = address,
            ProviderType = provider,
            SpecialImapProvider = special,
            AccountColorHex = color,
            Order = order,
            AttentionReason = AccountAttentionReason.None,
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true,
            IsCalendarAccessEnabled = true,
            IsContactAccessGranted = true,
            IsContactAccessEnabled = true,
            IsTaskAccessGranted = true,
            IsTaskAccessEnabled = true,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths
        };
        CustomServerInformation? server = provider != MailProviderType.IMAP4 ? null : new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Address = address,
            IncomingServer = "127.0.0.1",
            IncomingServerPort = "1",
            IncomingServerUsername = address,
            IncomingServerPassword = "demo",
            OutgoingServer = "127.0.0.1",
            OutgoingServerPort = "1",
            OutgoingServerUsername = address,
            OutgoingServerPassword = "demo",
            IncomingServerType = CustomIncomingServerType.IMAP4,
            MaxConcurrentClients = 1
        };
        await accounts.CreateAccountAsync(account, server);
        await accounts.CreateRootAliasAsync(account.Id, address);
        return account;
    }

    private static async Task<int> SeedMailAsync(IServiceProvider services, MailAccount account, DemoMail[] messages)
    {
        var folderService = services.GetRequiredService<IFolderService>();
        var folders = new (string Remote, string Name, SpecialFolderType Type)[]
        {
            ("INBOX", "Inbox", SpecialFolderType.Inbox), ("Drafts", "Drafts", SpecialFolderType.Draft), ("Sent", "Sent", SpecialFolderType.Sent),
            ("Archive", "Archive", SpecialFolderType.Archive), ("Junk", "Junk", SpecialFolderType.Junk), ("Trash", "Trash", SpecialFolderType.Deleted)
        };
        int order = 1;
        foreach (var (remote, name, type) in folders)
        {
            await folderService.InsertFolderAsync(new MailItemFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                RemoteFolderId = remote,
                ParentRemoteFolderId = string.Empty,
                FolderName = name,
                SpecialFolderType = type,
                IsSystemFolder = true,
                IsSticky = true,
                IsSynchronizationEnabled = true,
                ShowUnreadCount = true,
                IsCountedInAccountTotal = type == SpecialFolderType.Inbox,
                Order = order++
            });
        }

        var mail = services.GetRequiredService<IMailService>();
        // Anchored to late morning, so the newest mails carry daytime times wherever in the day the seed runs.
        var now = DateTime.Today.AddHours(DateTime.Now.Hour >= 11 ? 11.75 : -12.25);
        int created = 0;
        foreach (var message in messages)
        {
            var date = now.AddHours(-message.HoursAgo);
            var messageId = $"{Guid.NewGuid():N}@wino.test";
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(message.Name, message.Address));
            mime.To.Add(new MailboxAddress(account.SenderName, account.Address));
            mime.Subject = message.Subject;
            mime.Date = new DateTimeOffset(date);
            mime.MessageId = messageId;
            var html = new TextPart("html") { Text = string.Concat(message.Paragraphs.Select(paragraph => $"<p>{paragraph}</p>")) };
            if (message.Attachment is { } fileName)
            {
                var attachment = new MimePart(MimeTypes.GetMimeType(fileName))
                {
                    Content = new MimeContent(new System.IO.MemoryStream(new byte[48 * 1024])),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = fileName
                };
                mime.Body = new Multipart("mixed") { html, attachment };
            }
            else
            {
                mime.Body = html;
            }
            var copy = new MailCopy
            {
                Id = messageId,
                MessageId = messageId,
                ThreadId = $"thread-{Guid.NewGuid():N}",
                FromName = message.Name,
                FromAddress = message.Address,
                Subject = message.Subject,
                PreviewText = WebUtility.HtmlDecode(message.Preview),
                CreationDate = date.ToUniversalTime(),
                IsRead = message.IsRead,
                IsFlagged = message.IsFlagged,
                HasAttachments = message.Attachment is not null,
                IsFocused = true,
                FileId = Guid.NewGuid(),
                Importance = MailImportance.Normal
            };
            if (await mail.CreateMailAsync(account.Id, new NewMailItemPackage(copy, mime, "INBOX"))) created++;
        }
        return created;
    }

    private static async Task<int> SeedCalendarsAsync(IServiceProvider services, MailAccount outlook, MailAccount gmail, MailAccount icloud)
    {
        var calendars = services.GetRequiredService<ICalendarService>();
        var work = Calendar(outlook, "store-calendar", "Calendar", "#0078D4", primary: true);
        var team = Calendar(outlook, "store-team", "Team", "#8764B8", primary: false);
        var project = Calendar(gmail, "store-project", "Side project", "#E66C37", primary: true);
        var family = Calendar(icloud, "store-family", "Family", "#2E9E5B", primary: true);
        foreach (var calendar in new[] { work, team, project, family }) await calendars.InsertAccountCalendarAsync(calendar);

        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        int created = 0;
        async Task Add(AccountCalendar calendar, MailAccount owner, int day, double hour, double hours, string title, string location = "", bool attendees = false)
        {
            var item = new CalendarItem
            {
                Id = Guid.NewGuid(),
                RemoteEventId = "store-" + Guid.NewGuid().ToString("N"),
                Title = title,
                Description = string.Empty,
                Location = location,
                StartDate = monday.AddDays(day).AddHours(hour),
                DurationInSeconds = TimeSpan.FromHours(hours).TotalSeconds,
                StartTimeZone = TimeZoneInfo.Local.Id,
                EndTimeZone = TimeZoneInfo.Local.Id,
                OrganizerDisplayName = owner.SenderName,
                OrganizerEmail = owner.Address,
                CalendarId = calendar.Id,
                Status = CalendarItemStatus.Accepted,
                ShowAs = CalendarItemShowAs.Busy,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            var people = new List<CalendarEventAttendee>();
            if (attendees)
            {
                people.Add(Attendee(item, "Avery Chen", "avery.chen@contoso.example", AttendeeStatus.Accepted));
                people.Add(Attendee(item, "Jordan Rivera", "jordan.rivera@contoso.example", AttendeeStatus.Accepted));
                people.Add(Attendee(item, "Morgan Patel", "morgan.patel@woodgrove.example", AttendeeStatus.Tentative));
            }
            await calendars.CreateNewCalendarItemAsync(item, people);
            created++;
        }

        await Add(team, outlook, 0, 9.5, 0.5, "Stand-up", "Microsoft Teams");
        await Add(work, outlook, 0, 11, 1, "Design review", "Room 2", attendees: true);
        await Add(work, outlook, 0, 14, 1.5, "Focus time");
        await Add(work, outlook, 1, 10, 1.5, "Customer call", "Microsoft Teams");
        await Add(family, icloud, 1, 13, 1, "Lunch with Morgan", "Ramen Bar");
        await Add(team, outlook, 2, 9.5, 0.5, "Stand-up", "Microsoft Teams");
        await Add(family, icloud, 2, 12, 1, "Dentist");
        await Add(work, outlook, 2, 15, 1, "1:1 with Jordan");
        await Add(work, outlook, 3, 10, 1, "Project kickoff", "Conference room 2", attendees: true);
        await Add(project, gmail, 3, 14, 1.5, "Logo workshop", "Studio");
        await Add(team, outlook, 4, 9.5, 0.5, "Stand-up", "Microsoft Teams");
        await Add(team, outlook, 4, 12.5, 1, "Team lunch");
        await Add(project, gmail, 4, 16, 1, "Beta release");
        await Add(family, icloud, 5, 10, 2, "Farmers market");
        await Add(family, icloud, 6, 11, 2, "Brunch with Sam");
        await Add(family, icloud, 5, 0, 48, "Weekend at the cabin");
        return created;
    }

    private static async Task<int> SeedContactsAsync(IServiceProvider services, MailAccount account)
    {
        var contacts = services.GetRequiredService<IContactService>();
        var book = await contacts.EnsureLocalAddressBookAsync(account.Id, "Contacts");
        var people = new (string Given, string Surname, string Email, string Phone, string Company, string Job, int? Month, int? Day, bool Favorite)[]
        {
            ("Avery", "Chen", "avery.chen@contoso.example", "+1 555 0142", "Contoso", "Product designer", 3, 14, true),
            ("Alex", "Dvorak", "alex.dvorak@northwind.example", "+1 555 0177", "Northwind Travel", "Travel advisor", null, null, false),
            ("Emma", "Lindqvist", "emma@northwind.example", "+46 8 555 0118", "Northwind", "Engineering manager", 7, 2, false),
            ("Jamie", "Lindqvist", "jamie@fabrikam.example", "+1 555 0163", "Fabrikam", "Account manager", null, null, false),
            ("Jordan", "Rivera", "jordan.rivera@contoso.example", "+1 555 0198", "Contoso", "Engineering lead", 9, 21, true),
            ("Lucas", "Becker", "lucas@adatum.example", "+49 30 555 0144", "Adatum", "Brand designer", null, null, false),
            ("Mika", "Okafor", "mika@tailspin.example", "+1 555 0120", "Tailspin Toys", "Design director", 5, 30, false),
            ("Morgan", "Patel", "morgan.patel@woodgrove.example", "+1 555 0131", "Woodgrove", "Researcher", 11, 4, true),
            ("Noah", "Fischer", "noah.fischer@icloud.com", "+1 555 0185", "Family", "Brother", 2, 9, false),
            ("Riley", "Santos", "riley.santos@contoso.example", "+1 555 0109", "Contoso", "Finance analyst", null, null, false),
            ("Sam", "Kim", "sam.kim@outlook.com", "+1 555 0156", "Family", "Partner", 6, 18, true),
            ("Sasha", "Novak", "sasha@adventure-works.example", "+1 555 0193", "Adventure Works", "Photographer", null, null, false)
        };
        var seeded = people.Select(person =>
        {
            var contact = new AccountContact
            {
                MailAccountId = account.Id,
                AddressBookId = book.Id,
                SourceKind = book.SourceKind,
                RemoteId = "store-" + person.Email,
                GivenName = person.Given,
                Surname = person.Surname,
                DisplayName = $"{person.Given} {person.Surname}",
                CompanyName = person.Company,
                JobTitle = person.Job,
                BirthdayMonth = person.Month,
                BirthdayDay = person.Day,
                IsFavorite = person.Favorite,
                Notes = person.Given == "Avery" ? "Prefers a call on Fridays." : string.Empty
            };
            contact.EmailAddresses.Add(new ContactEmailAddress { ContactId = contact.Id, Address = person.Email, NormalizedAddress = ContactEmailAddress.Normalize(person.Email), Label = "Work", IsPrimary = true });
            contact.PhoneNumbers.Add(new ContactPhoneNumber { ContactId = contact.Id, Number = person.Phone, Kind = ContactPhoneKind.Mobile, IsPrimary = true });
            return contact;
        }).ToList();
        await contacts.ReplaceAddressBookAsync(book.Id, seeded, string.Empty);
        return seeded.Count;
    }

    private static async Task<int> SeedTasksAsync(IServiceProvider services, MailAccount account)
    {
        var tasks = services.GetRequiredService<ITaskService>();
        var work = await tasks.GetOrCreateLocalTaskListAsync(account.Id, "Work");
        var groceries = await tasks.CreateTaskListAsync(account.Id, "Groceries");
        var home = await tasks.CreateTaskListAsync(account.Id, "Home");
        var trip = await tasks.CreateTaskListAsync(account.Id, "Trip to Lisbon");
        var today = DateTime.Now.Date;
        int created = 0;
        async Task Add(AccountTaskList list, string title, DateTime? due = null, bool important = false, bool myDay = false, string? notes = null,
            bool done = false, params (string Title, bool Done)[] steps)
        {
            var task = new AccountTask
            {
                MailAccountId = account.Id,
                TaskListId = list.Id,
                SourceKind = list.SourceKind,
                Title = title,
                DueDate = due,
                IsImportant = important,
                MyDayDateUtc = myDay ? DateTime.UtcNow.Date : null,
                Notes = notes ?? string.Empty,
                IsCompleted = done,
                CompletedAtUtc = done ? DateTime.UtcNow : null
            };
            await tasks.CreateTaskAsync(task);
            int order = 0;
            foreach (var step in steps)
                await tasks.CreateStepAsync(new AccountTaskStep { TaskId = task.Id, MailAccountId = account.Id, SourceKind = list.SourceKind, Title = step.Title, IsCompleted = step.Done, Order = order++ });
            created++;
        }

        await Add(work, "Prepare kickoff slides", today, important: true, myDay: true, notes: "Keep it to ten slides. Avery asked for the timeline first.",
            steps: [("Collect the numbers from Jordan", true), ("Draft the timeline slide", false), ("Rehearse once", false)]);
        await Add(work, "Book team lunch", myDay: true);
        await Add(home, "Renew passport", today.AddDays(1), important: true, myDay: true);
        await Add(work, "Send invoice to Northwind", today.AddDays(4), myDay: true);
        await Add(work, "Reply to Avery", myDay: true, done: true);
        await Add(groceries, "Oat milk");
        await Add(groceries, "Coffee beans", myDay: true);
        await Add(groceries, "Basil and tomatoes");
        await Add(home, "Call the plumber", today.AddDays(2), myDay: true);
        await Add(trip, "Check in for the flight", today.AddDays(5), important: true);
        await Add(trip, "Pack the camera charger");
        return created;
    }

    private static AccountCalendar Calendar(MailAccount account, string remoteId, string name, string color, bool primary) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        RemoteCalendarId = remoteId,
        Name = name,
        IsPrimary = primary,
        IsReadOnly = false,
        IsSynchronizationEnabled = true,
        IsExtended = true,
        BackgroundColorHex = color,
        TextColorHex = "#FFFFFF",
        TimeZone = TimeZoneInfo.Local.Id
    };

    private static CalendarEventAttendee Attendee(CalendarItem item, string name, string email, AttendeeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        CalendarItemId = item.Id,
        Name = name,
        Email = email,
        AttendenceStatus = status
    };
}
#endif
