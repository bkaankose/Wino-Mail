#if DEBUG
#nullable enable
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Diagnostics;

/// <summary>
/// Debug-only local data for the Contacts and To Do modes on simulators and emulators (the iOS bridge commands
/// <c>seedpeople</c> and <c>seedtasks</c>, the Android debug commands <c>seedcontacts</c> and <c>seedtasks</c>), through
/// the shared services. Both use the first account, or create the <see cref="MailDebugSeed"/> demo account when there is
/// none, grant the mode on it, and fill a local address book (12 contacts) or two local task lists (6 tasks with due dates,
/// My Day, importance, steps, notes and one completed). The caller reopens the mode so the page singletons reload.
/// </summary>
public static class ModeDebugSeed
{
    public static async Task<string> SeedContactsAsync(IServiceProvider services)
    {
        var account = await AccountAsync(services, item => { item.IsContactAccessGranted = true; item.IsContactAccessEnabled = true; });
        if (account is null) return "no account";
        var contacts = services.GetRequiredService<IContactService>();
        var book = await contacts.EnsureLocalAddressBookAsync(account.Id, "Wino Demo");
        var people = new (string Given, string Surname, string Email, string? Phone, string Company, string Job, int? Month, int? Day, bool Favorite)[]
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
            ("Hedy", "Lamarr", "hedy@example.com", null, "Independent", "Inventor", 11, 9, true),
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
                Notes = person.Favorite ? "Seeded by the debug command." : string.Empty
            };
            contact.EmailAddresses.Add(new ContactEmailAddress { ContactId = contact.Id, Address = person.Email, NormalizedAddress = ContactEmailAddress.Normalize(person.Email), Label = "Work", IsPrimary = true });
            if (person.Phone is { } phone) contact.PhoneNumbers.Add(new ContactPhoneNumber { ContactId = contact.Id, Number = phone, Kind = ContactPhoneKind.Mobile, IsPrimary = true });
            return contact;
        }).ToList();
        await contacts.ReplaceAddressBookAsync(book.Id, seeded, string.Empty);
        return $"seeded {seeded.Count} contacts into {book.Id}";
    }

    public static async Task<string> SeedTasksAsync(IServiceProvider services)
    {
        var account = await AccountAsync(services, item => { item.IsTaskAccessGranted = true; item.IsTaskAccessEnabled = true; });
        if (account is null) return "no account";
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
        await AddAsync(release, "Review Contacts and To Do", today, true, true, "Check the phone and tablet layouts.", false,
            ("Contacts list and card", true), ("Contact editor", false), ("To Do lists and detail", false));
        await AddAsync(release, "Prepare 2.2 release notes", today.AddDays(3), true, steps: [("Draft", true), ("Screenshots", false), ("Translate", false)]);
        await AddAsync(release, "Book venue for team dinner", myDay: true, done: true);
        await AddAsync(inbox, "Send Q4 roadmap notes", today.AddDays(-1), notes: "Attach the slide deck.");
        await AddAsync(inbox, "Renew developer membership", today.AddDays(2), myDay: true);
        await AddAsync(inbox, "Buy milk");
        return $"seeded {created} tasks into {inbox.Title} and {release.Title}";
    }

    /// <summary>The first account with the mode granted; the demo account when there is none.</summary>
    private static async Task<MailAccount?> AccountAsync(IServiceProvider services, Action<MailAccount> grant)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        if ((await accounts.GetAccountsAsync()).Count == 0) await MailDebugSeed.SeedAsync(services);
        var account = (await accounts.GetAccountsAsync()).FirstOrDefault();
        if (account is null) return null;
        grant(account);
        await accounts.UpdateAccountAsync(account);
        return account;
    }
}
#endif
