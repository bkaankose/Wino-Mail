#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// Debug-only bridge commands that seed local data for the Contacts and To Do modes in the Simulator (no real account can be
/// added there), through the shared services: <c>seedpeople</c> grants People on the first account (K's <c>seedmail</c>
/// demo account) and fills a local address book; <c>seedtasks</c> grants To Do and fills two local lists with tasks and
/// steps. Both then reopen the mode so the singletons reload.
/// </summary>
internal sealed class ContactsDebugCommands(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        UIKitDebugBridge.Register("seedpeople", _ => SeedPeopleAsync());
        UIKitDebugBridge.Register("seedtasks", _ => SeedTasksAsync());
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    private async Task<MailAccount?> AccountAsync(Action<MailAccount> grant)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        var account = (await accounts.GetAccountsAsync()).FirstOrDefault() ?? await CreateDemoAccountAsync(accounts);
        if (account is null) return null;
        grant(account);
        await accounts.UpdateAccountAsync(account);
        return account;
    }

    /// <summary>
    /// The iPad has no <c>seedmail</c> (phone-only, K): a bare local demo IMAP account like K's (closed loopback port,
    /// placeholder test credentials, no folders), so the shell can show People and To Do.
    /// </summary>
    private static async Task<MailAccount?> CreateDemoAccountAsync(IAccountService accounts)
    {
        const string address = "demo@wino.test";
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Wino Demo",
            SenderName = "Wino Demo",
            Address = address,
            ProviderType = MailProviderType.IMAP4,
            AccountColorHex = "#0078D4",
            AttentionReason = AccountAttentionReason.None,
            IsMailAccessGranted = true,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths
        };
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            DisplayName = "Wino Demo",
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
        return (await accounts.GetAccountsAsync()).FirstOrDefault();
    }

    private async Task ReopenAsync(WinoApplicationMode mode)
    {
        var navigation = services.GetRequiredService<UIKitNavigationService>();
        navigation.ChangeApplicationMode(WinoApplicationMode.Mail);
        await Task.Delay(800);
        navigation.ChangeApplicationMode(mode);
    }

    private async Task<string> SeedPeopleAsync()
    {
        var account = await AccountAsync(item => { item.IsContactAccessGranted = true; item.IsContactAccessEnabled = true; });
        if (account is null) return "no account: run seedmail first";
        var contacts = services.GetRequiredService<IContactService>();
        var book = await contacts.EnsureLocalAddressBookAsync(account.Id, "Wino Demo");
        var people = new (string Given, string Surname, string Email, string? Phone, string? Company, string? Job, int? Month, int? Day, bool Favorite)[]
        {
            ("Ada", "Lovelace", "ada@example.com", "+44 20 7946 0001", "Analytical Engines", "Mathematician", 12, 10, true),
            ("Alan", "Turing", "alan@example.com", "+44 20 7946 0002", "Bletchley Park", "Cryptanalyst", 6, 23, false),
            ("Grace", "Hopper", "grace@example.com", "+1 202 555 0103", "US Navy", "Rear Admiral", 12, 9, true),
            ("Katherine", "Johnson", "katherine@example.com", "+1 757 555 0104", "NASA", "Mathematician", 8, 26, false),
            ("Linus", "Torvalds", "linus@example.com", null, "Linux Foundation", "Fellow", null, null, false),
            ("Margaret", "Hamilton", "margaret@example.com", "+1 617 555 0106", "MIT", "Director of Software Engineering", 8, 17, false),
            ("Barbara", "Liskov", "barbara@example.com", null, "MIT", "Professor", 11, 7, false),
            ("Dennis", "Ritchie", "dennis@example.com", "+1 908 555 0108", "Bell Labs", "Computer Scientist", 9, 9, false),
            ("Edsger", "Dijkstra", "edsger@example.com", null, "UT Austin", "Professor", 5, 11, false),
            ("Frances", "Allen", "frances@example.com", "+1 914 555 0110", "IBM", "Fellow", 8, 4, false),
            ("Hedy", "Lamarr", "hedy@example.com", null, null, "Inventor", 11, 9, true),
            ("Tim", "Berners-Lee", "tim@example.com", "+44 20 7946 0012", "W3C", "Director", 6, 8, false)
        };
        var seeded = people.Select(person =>
        {
            var contact = new AccountContact
            {
                MailAccountId = account.Id,
                AddressBookId = book.Id,
                SourceKind = book.SourceKind,
                RemoteId = "demo-" + person.Email,
                GivenName = person.Given,
                Surname = person.Surname,
                DisplayName = $"{person.Given} {person.Surname}",
                CompanyName = person.Company,
                JobTitle = person.Job,
                BirthdayMonth = person.Month,
                BirthdayDay = person.Day,
                IsFavorite = person.Favorite,
                Notes = person.Favorite ? "Seeded by the iOS debug bridge." : null
            };
            contact.EmailAddresses.Add(new ContactEmailAddress { ContactId = contact.Id, Address = person.Email, NormalizedAddress = ContactEmailAddress.Normalize(person.Email), Label = "Work", IsPrimary = true });
            if (person.Phone is { } phone) contact.PhoneNumbers.Add(new ContactPhoneNumber { ContactId = contact.Id, Number = phone, Kind = ContactPhoneKind.Mobile, IsPrimary = true });
            return contact;
        }).ToList();
        await contacts.ReplaceAddressBookAsync(book.Id, seeded, string.Empty);
        await ReopenAsync(WinoApplicationMode.Contacts);
        return $"seeded {seeded.Count} contacts into {book.Id}";
    }

    private async Task<string> SeedTasksAsync()
    {
        var account = await AccountAsync(item => { item.IsTaskAccessGranted = true; item.IsTaskAccessEnabled = true; });
        if (account is null) return "no account: run seedmail first";
        var tasks = services.GetRequiredService<ITaskService>();
        var inbox = await tasks.GetOrCreateLocalTaskListAsync(account.Id, "Tasks");
        var release = await tasks.CreateTaskListAsync(account.Id, "Wino releases");
        var today = DateTime.Now.Date;
        int created = 0;
        async Task AddAsync(AccountTaskList list, string title, DateTime? due = null, bool important = false, bool myDay = false, string? notes = null,
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
        await AddAsync(release, "Review iOS Contacts and To Do", today, true, true, "Check the phone and pad layouts.", false,
            ("Contacts list and card", true), ("Contact editor sheet", false), ("To Do lists and detail", false));
        await AddAsync(release, "Prepare 2.2 release notes", today.AddDays(3), true, steps: [("Draft", true), ("Screenshots", false), ("Translate", false)]);
        await AddAsync(release, "Book venue for team dinner", done: true);
        await AddAsync(inbox, "Send Q4 roadmap notes", today.AddDays(-1), notes: "Attach the slide deck.");
        await AddAsync(inbox, "Renew developer membership", today.AddDays(2), myDay: true);
        await AddAsync(inbox, "Buy milk");
        await ReopenAsync(WinoApplicationMode.Tasks);
        return $"seeded {created} tasks into {inbox.Title} and {release.Title}";
    }
}
#endif
