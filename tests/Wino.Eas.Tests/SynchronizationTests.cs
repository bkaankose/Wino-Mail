using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Eas;
using Wino.Services;
using Wino.Services.Platform;

internal static class SynchronizationTests
{
    private static readonly XNamespace Sync = "AirSync:";
    private static readonly XNamespace Calendar = "Calendar:";
    private static readonly XNamespace Folder = "FolderHierarchy:";
    private static int _checks;

    internal static async Task RunAsync()
    {
        await CreateAndEchoAsync(false);
        await CreateAndEchoAsync(true);
        await CallbackHoldsGateAsync();
        await FailedCreateAsync();
        await CalendarIsolationAsync();
        await RepairExistingDuplicateAsync();
        await InvalidSyncKeyAsync();
        await MissingTasksAsync();
        await TasksAndContactsAsync();
        await MailPersistenceAsync();
        await FailedMailPersistenceAsync();
        await ProviderCalendarRequestsAsync();
        await LocalDraftCompletionAsync();
        Console.WriteLine($"EAS synchronization/persistence: {_checks} checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(name);
    }

    private static XElement EventData(Guid uid, string subject = "Test appointment") => new(Sync + "ApplicationData",
        new XElement(Calendar + "UID", uid.ToString("N")), new XElement(Calendar + "Subject", subject),
        new XElement(Calendar + "StartTime", "20261106T100000Z"), new XElement(Calendar + "EndTime", "20261106T110000Z"),
        new XElement(Calendar + "AllDayEvent", "0"), new XElement(Calendar + "Reminder", "15"),
        new XElement(Calendar + "Attendees", new XElement(Calendar + "Attendee",
            new XElement(Calendar + "Email", "attendee@example.test"), new XElement(Calendar + "Name", "Test attendee"))));

    private static XElement Command(string operation, string id, XElement? data = null)
        => new(Sync + operation, new XElement(Sync + "ServerId", id), data);

    private static XElement Page(string key, params XElement[] commands)
        => new(Sync + "Collection", new XElement(Sync + "SyncKey", key), new XElement(Sync + "Status", "1"),
            new XElement(Sync + "Commands", commands));

    private static async Task CreateAndEchoAsync(bool echoInCreateResponse)
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("calendar", 8);
        var uid = Guid.NewGuid();
        var data = EventData(uid);
        var calendar = await fixture.Calendars.GetAccountCalendarAsync(EasDataMapper.StableId(state.Id));
        var local = EasDataMapper.ReadEvent(data, calendar, string.Empty);
        local.Id = uid;
        var attendees = EasDataMapper.ReadAttendees(data, uid);
        var reminders = new List<Reminder> { new() { Id = Guid.NewGuid(), CalendarItemId = uid, DurationInSeconds = 900 } };
        fixture.Server.Sync("1", Page("2"));
        fixture.Server.Expect("Sync", request =>
        {
            var command = request.Descendants(Sync + "Add").Single();
            Check(command.Element(Sync + "ClientId")?.Value == uid.ToString("N"), "Create sends the client ID");
            Check(command.Element(Sync + "ApplicationData")?.Element(Calendar + "UID")?.Value == uid.ToString("N"), "Create sends the calendar UID");
            var response = Page("3", echoInCreateResponse ? new[] { Command("Add", "echo-id", data) } : Array.Empty<XElement>());
            response.Add(new XElement(Sync + "Responses", new XElement(Sync + "Add",
                new XElement(Sync + "Status", "1"), new XElement(Sync + "ClientId", uid.ToString("N")), new XElement(Sync + "ServerId", "creation-id"))));
            return response;
        });
        await fixture.Engine.CreateCalendarEventAsync(state.RemoteId, data, uid.ToString("N"),
            id => fixture.Processor.PersistCreatedCalendarEventAsync(local, attendees, reminders, id.WithClientTrackingId(uid)), default);
        Check(await fixture.CountAsync<CalendarItem>() == 1, "Create and immediate echo leave one event");
        Check(await fixture.CountAsync<CalendarEventAttendee>() == 1 && await fixture.CountAsync<Reminder>() == 1, "Create and echo preserve one attendee and reminder");

        fixture.Server.Folders();
        if (echoInCreateResponse)
            fixture.Server.Sync("0", Page("reset"));
        fixture.Server.Sync(echoInCreateResponse ? "reset" : "3", Page("4", Command("Add", "echo-id", data)));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        var saved = await fixture.Calendars.GetCalendarItemAsync(calendar.Id, "echo-id");
        Check(saved?.Id == uid && saved.RemoteEventId.GetClientTrackingId() == uid, "Delayed echo retains client identity despite changed server ID");
        Check(await fixture.CountAsync<CalendarItem>() == 1, "Delayed echo does not duplicate the local event");

        // A new engine has no in-memory identity state, like an application restart.
        fixture.RestartEngine();
        fixture.Server.Folders();
        if (!echoInCreateResponse)
            fixture.Server.Sync("0", Page("reset"));
        fixture.Server.Sync(echoInCreateResponse ? "4" : "reset", Page("5", Command("Add", "echo-id", EventData(uid, "Updated remotely"))));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        fixture.Server.Folders();
        fixture.Server.Sync("5", Page("6", Command("Change", "echo-id", EventData(uid, "Updated twice"))));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        saved = await fixture.Calendars.GetCalendarItemAsync(uid);
        Check(saved?.Title == "Updated twice" && saved.RemoteEventId.GetProviderRemoteEventId() == "echo-id", "Restart, reconciliation and change retain the original row");
        Check(await fixture.CountAsync<CalendarItem>() == 1 && await fixture.CountAsync<EasItemShadow>() == 1, "Reset removes the obsolete shadow without deleting the event");
        Check(await fixture.CountAsync<CalendarEventAttendee>() == 1 && await fixture.CountAsync<Reminder>() == 1, "Repeated sync does not duplicate dependent rows");
        fixture.Server.Folders();
        fixture.Server.Sync("6", Page("7", Command("Delete", "echo-id")));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        Check(await fixture.CountAsync<CalendarItem>() == 0 && await fixture.CountAsync<CalendarEventAttendee>() == 0 && await fixture.CountAsync<Reminder>() == 0,
            "Remote deletion removes the tracked event and dependent rows");
        fixture.Server.AssertDrained();
    }

    private static async Task CallbackHoldsGateAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("calendar", 8);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Sync("1", Page("2"));
        var response = Page("3");
        response.Add(new XElement(Sync + "Responses", new XElement(Sync + "Add", new XElement(Sync + "Status", "1"), new XElement(Sync + "ServerId", "created"))));
        fixture.Server.Sync("2", response);
        var create = fixture.Engine.CreateCalendarEventAsync(state.RemoteId, EventData(Guid.NewGuid()), "client", async _ =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var requests = fixture.Server.Requests;
        fixture.Server.Folders();
        fixture.Server.Sync("3", Page("4"));
        var concurrentSync = fixture.Engine.SynchronizeAsync("Calendar", default);
        Check(fixture.Server.Requests == requests && !concurrentSync.IsCompleted, "Concurrent sync cannot project before the create callback completes");
        release.SetResult();
        await Task.WhenAll(create, concurrentSync).WaitAsync(TimeSpan.FromSeconds(10));
        Check(await fixture.StoredKeyAsync(state) == "4", "Concurrent sync proceeds after local persistence");
        fixture.Server.AssertDrained();
    }

    private static async Task FailedCreateAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("calendar", 8);
        fixture.Server.Sync("1", Page("2"));
        var rejected = Page("3");
        rejected.Add(new XElement(Sync + "Responses", new XElement(Sync + "Add", new XElement(Sync + "Status", "6"))));
        fixture.Server.Sync("2", rejected);
        var callbacks = 0;
        await ThrowsAsync<EasCommandException>(() => fixture.Engine.CreateCalendarEventAsync(state.RemoteId, EventData(Guid.NewGuid()), "client",
            _ => { callbacks++; return Task.CompletedTask; }, default), "Rejected addition must fail");
        Check(callbacks == 0 && await fixture.StoredKeyAsync(state) == "2", "Rejected create neither persists an event nor advances the response key");
        Check(await fixture.CountAsync<CalendarItem>() == 0 && await fixture.CountAsync<EasItemShadow>() == 0, "Rejected create leaves no local projection");
        fixture.Server.Sync("2", Page("4"));
        var accepted = Page("5");
        accepted.Add(new XElement(Sync + "Responses", new XElement(Sync + "Add", new XElement(Sync + "Status", "1"), new XElement(Sync + "ServerId", "created"))));
        fixture.Server.Sync("4", accepted);
        await ThrowsAsync<IOException>(() => fixture.Engine.CreateCalendarEventAsync(state.RemoteId, EventData(Guid.NewGuid()), "client",
            _ => throw new IOException("Simulated local persistence failure"), default), "Local persistence failure must propagate");
        Check(await fixture.StoredKeyAsync(state) == "4" && await fixture.CountAsync<EasItemShadow>() == 0, "Persistence failure does not commit the create key or shadow");
        fixture.Server.AssertDrained();
    }

    private static async Task CalendarIsolationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddCollectionAsync("first", 8);
        var second = await fixture.AddCollectionAsync("second", 13);
        var uid = Guid.NewGuid();
        var firstCalendar = await fixture.Calendars.GetAccountCalendarAsync(EasDataMapper.StableId(first.Id));
        var original = EasDataMapper.ReadEvent(EventData(uid), firstCalendar, "first-event");
        original.Id = uid;
        await fixture.Calendars.CreateNewCalendarItemAsync(original, new());
        fixture.Server.Folders();
        fixture.Server.Sync("1", Page("2"));
        fixture.Server.Sync("1", Page("2", Command("Add", "other-calendar-event", EventData(uid))));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        var other = await fixture.Calendars.GetCalendarItemAsync(EasDataMapper.StableId(second.Id), "other-calendar-event");
        Check(other != null && other.Id != uid && other.CalendarId != original.CalendarId, "UID matching never merges across calendars");
        Check((await fixture.Calendars.GetCalendarItemAsync(uid))?.CalendarId == original.CalendarId, "Other calendar cannot move or overwrite the original event");
        var foreign = EventData(Guid.NewGuid());
        foreign.SetElementValue(Calendar + "UID", "foreign-calendar-uid@example.test");
        fixture.Server.Folders();
        fixture.Server.Sync("2", Page("3"));
        fixture.Server.Sync("2", Page("3", Command("Add", "foreign", foreign)));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        fixture.Server.Folders();
        fixture.Server.Sync("3", Page("4"));
        fixture.Server.Sync("3", Page("4", Command("Add", "foreign", foreign)));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        Check(await fixture.CountAsync<CalendarItem>() == 3, "Foreign non-GUID UIDs remain distinct and replay is idempotent");
        fixture.Server.AssertDrained();
    }

    private static async Task RepairExistingDuplicateAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("calendar", 8);
        var uid = Guid.NewGuid();
        var data = EventData(uid);
        var calendar = await fixture.Calendars.GetAccountCalendarAsync(EasDataMapper.StableId(state.Id));
        var local = EasDataMapper.ReadEvent(data, calendar, "old".WithClientTrackingId(uid));
        local.Id = uid;
        await fixture.Calendars.CreateNewCalendarItemAsync(local, new());
        var duplicate = EasDataMapper.ReadEvent(data, calendar, "echo");
        await fixture.Calendars.CreateNewCalendarItemAsync(duplicate, EasDataMapper.ReadAttendees(data, duplicate.Id));
        await fixture.Calendars.SaveRemindersAsync(duplicate.Id, new() { new Reminder { Id = Guid.NewGuid(), CalendarItemId = duplicate.Id, DurationInSeconds = 900 } });
        foreach (var remoteId in new[] { "old", "echo" })
            await fixture.Database.Connection.InsertAsync(new EasItemShadow
            {
                Id = EasDataMapper.Key(fixture.Account.Id, state.RemoteId, remoteId),
                AccountId = fixture.Account.Id,
                CollectionId = state.Id,
                RemoteId = remoteId,
                ApplicationData = data.ToString()
            }, typeof(EasItemShadow));
        fixture.Server.Folders();
        fixture.Server.Sync("0", Page("reset"));
        fixture.Server.Sync("reset", Page("2", Command("Add", "echo", data)));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        Check(await fixture.CountAsync<CalendarItem>() == 1 && (await fixture.Calendars.GetCalendarItemAsync(uid))?.Id == uid, "Existing duplicate reconciles to the original local ID");
        Check(await fixture.CountAsync<EasItemShadow>() == 1 && await fixture.CountAsync<CalendarEventAttendee>() == 1 && await fixture.CountAsync<Reminder>() == 1,
            "Duplicate cleanup removes stale shadows and dependent rows");
        Check(fixture.Server.Mutations == 0, "Local duplicate repair never sends a remote mutation or deletion");
        fixture.Server.AssertDrained();
    }

    private static async Task InvalidSyncKeyAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("calendar", 8);
        fixture.Server.Folders();
        fixture.Server.Sync("1", Page("2", Command("Add", "stale", EventData(Guid.NewGuid()))));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        fixture.Server.Folders();
        fixture.Server.Sync("2", new XElement(Sync + "Collection", new XElement(Sync + "Status", "3")));
        fixture.Server.Sync("0", Page("reset"));
        var firstPage = Page("3", Command("Add", "retained", EventData(Guid.NewGuid())));
        firstPage.Add(new XElement(Sync + "MoreAvailable"));
        fixture.Server.Sync("reset", firstPage);
        fixture.Server.Sync("3", Page("4"));
        await fixture.Engine.SynchronizeAsync("Calendar", default);
        Check(await fixture.CountAsync<CalendarItem>() == 1 && await fixture.Calendars.GetCalendarItemAsync(EasDataMapper.StableId(state.Id), "stale") == null,
            "Invalid key recovery removes only stale projections after the final page");
        Check(await fixture.StoredKeyAsync(state) == "4", "Paged recovery commits the final key");
        fixture.Server.AssertDrained();
    }

    private static async Task MissingTasksAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Folders();
        await ThrowsAsync<NotSupportedException>(() => fixture.Engine.SynchronizeAsync("Tasks", default), "Missing Tasks collection must not report success");
        Check(await fixture.CountAsync<AccountTaskList>() == 0, "Missing provider collection never creates a local task list");
        fixture.Server.AssertDrained();
    }

    private static async Task TasksAndContactsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Count is a FolderSync metadata element, not a collection change.
        fixture.Server.Expect("FolderSync", _ => new XElement(Folder + "FolderSync", new XElement(Folder + "Status", "1"),
            new XElement(Folder + "SyncKey", "folders"), new XElement(Folder + "Changes", new XElement(Folder + "Count", "2"),
                FolderChange("tasks", 7), FolderChange("contacts", 9))));
        XNamespace tasks = "Tasks:";
        var task = new XElement(Sync + "ApplicationData", new XElement(tasks + "Subject", "Task one"), new XElement(tasks + "Complete", "0"));
        fixture.Server.Sync("0", Page("handshake"));
        fixture.Server.Sync("handshake", Page("2", Command("Add", "task", task)));
        await fixture.Engine.SynchronizeAsync("Tasks", default);
        var list = (await fixture.Tasks.GetTaskListsAsync(fixture.Account.Id)).Single();
        Check(list.SourceKind == TaskSourceKind.Eas && list.RemoteId == "tasks", "FolderSync discovers the provider task list");
        Check(await fixture.CountAsync<AccountTask>() == 1, "Task Add persists to SQLite");
        XNamespace contacts = "Contacts:";
        var contact = new XElement(Sync + "ApplicationData", new XElement(contacts + "FirstName", "Initial"), new XElement(contacts + "Email1Address", "contact@example.test"));
        fixture.Server.Folders();
        fixture.Server.Sync("0", Page("handshake"));
        fixture.Server.Sync("handshake", Page("2", Command("Add", "contact", contact)));
        await fixture.Engine.SynchronizeAsync("Contacts", default);
        Check(await fixture.CountAsync<AccountContact>() == 1, "Contact Add persists to SQLite");
        task.SetElementValue(tasks + "Subject", "Task updated");
        task.SetElementValue(tasks + "Complete", "1");
        fixture.Server.Folders();
        fixture.Server.Sync("2", Page("3", Command("Change", "task", task)));
        await fixture.Engine.SynchronizeAsync("Tasks", default);
        var storedTask = (await fixture.Database.Connection.Table<AccountTask>().ToListAsync()).Single();
        Check(storedTask.Title == "Task updated" && storedTask.IsCompleted, "Task Change updates rather than duplicates");
        contact.SetElementValue(contacts + "FirstName", "Updated");
        fixture.Server.Folders();
        fixture.Server.Sync("2", Page("3", Command("Change", "contact", contact)));
        await fixture.Engine.SynchronizeAsync("Contacts", default);
        var storedContact = (await fixture.Database.Connection.Table<AccountContact>().ToListAsync()).Single();
        Check(storedContact.GivenName == "Updated", "Contact Change updates rather than duplicates");
        fixture.Server.Folders();
        fixture.Server.Sync("3", Page("4", Command("Delete", "task")));
        await fixture.Engine.SynchronizeAsync("Tasks", default);
        fixture.Server.Folders();
        fixture.Server.Sync("3", Page("4", Command("Delete", "contact")));
        await fixture.Engine.SynchronizeAsync("Contacts", default);
        Check(await fixture.CountAsync<AccountTask>() == 0 && await fixture.CountAsync<AccountContact>() == 0, "Provider deletions remove tasks and contacts");
        fixture.Server.AssertDrained();
    }

    private static XElement FolderChange(string id, int type) => new(Folder + "Add", new XElement(Folder + "ServerId", id),
        new XElement(Folder + "ParentId", "0"), new XElement(Folder + "DisplayName", id), new XElement(Folder + "Type", type));

    private static XElement MailData() => new(Sync + "ApplicationData",
        new XElement("{Email:}Read", "0"), new XElement("{AirSyncBase:}Body", new XElement("{AirSyncBase:}Type", "4"),
            new XElement("{AirSyncBase:}Data", "From: sender@example.test\r\nTo: user@example.test\r\nSubject: Persistence test\r\nMessage-ID: <test@example.test>\r\nDate: Fri, 06 Nov 2026 10:00:00 +0000\r\n\r\nTest body")));

    private static async Task MailPersistenceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Account.IsMailAccessGranted = true;
        var state = await fixture.AddCollectionAsync("inbox", 2);
        fixture.Server.Folders();
        fixture.Server.Sync("1", Page("2", Command("Add", "mail", MailData())));
        var downloaded = await fixture.Engine.SynchronizeAsync("Email", default);
        var copy = (await fixture.Database.Connection.Table<MailCopy>().ToListAsync()).Single();
        Check(downloaded.Count == 1 && copy.UniqueId != EasDataMapper.StableId(EasDataMapper.Key(fixture.Account.Id, "inbox", "mail")),
            "Real MailService assigns its own persisted local identity");
        var localId = copy.UniqueId;
        var fileId = copy.FileId;
        Check(await fixture.Mime.IsMimeExistAsync(fixture.Account.Id, fileId), "Mail MIME is saved to the isolated test directory");
        fixture.Server.Folders();
        fixture.Server.Sync("2", Page("3", Command("Add", "mail", MailData())));
        await fixture.Engine.SynchronizeAsync("Email", default);
        copy = (await fixture.Database.Connection.Table<MailCopy>().ToListAsync()).Single();
        Check(copy.UniqueId == localId && copy.FileId == fileId, "Replayed mail Add retains persisted identity and MIME file");
        fixture.Server.Folders();
        fixture.Server.Sync("3", Page("4", Command("Change", "mail", new XElement(Sync + "ApplicationData", new XElement("{Email:}Read", "1")))));
        await fixture.Engine.SynchronizeAsync("Email", default);
        copy = (await fixture.Database.Connection.Table<MailCopy>().ToListAsync()).Single();
        Check(copy.IsRead && copy.UniqueId == localId, "Flag-only sync finds mail by provider ID and folder");
        Check(await fixture.StoredKeyAsync(state) == "4", "Successful mail projection commits its sync key");
        fixture.Server.Folders();
        fixture.Server.Sync("4", Page("5", Command("Delete", "mail")));
        await fixture.Engine.SynchronizeAsync("Email", default);
        Check(await fixture.CountAsync<MailCopy>() == 0, "Mail Delete removes the persisted copy");
        fixture.Server.AssertDrained();
    }

    private static async Task FailedMailPersistenceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Account.IsMailAccessGranted = true;
        var state = await fixture.AddCollectionAsync("inbox", 2);
        await fixture.Database.Connection.DeleteAsync<MailAccount>(fixture.Account.Id);
        fixture.Server.Folders();
        fixture.Server.Sync("1", Page("2", Command("Add", "mail", MailData())));
        await ThrowsAsync<IOException>(() => fixture.Engine.SynchronizeAsync("Email", default), "Failed mail persistence must propagate");
        Check(await fixture.StoredKeyAsync(state) == "1" && await fixture.CountAsync<EasItemShadow>() == 0, "Failed mail persistence does not advance key or shadow");
        fixture.Server.AssertDrained();
    }

    private static async Task ProviderCalendarRequestsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        var state = await fixture.AddCollectionAsync("calendar", 8);
        var calendar = await fixture.Calendars.GetAccountCalendarAsync(EasDataMapper.StableId(state.Id));
        var provider = fixture.CreateProvider();
        try
        {
            var request = new CreateCalendarEventRequest(new CalendarEventComposeResult
            {
                AccountId = fixture.Account.Id,
                CalendarId = calendar.Id,
                Title = "Created through provider",
                StartDate = new DateTime(2026, 11, 6, 10, 0, 0),
                EndDate = new DateTime(2026, 11, 6, 11, 0, 0),
                TimeZoneId = "UTC"
            }, calendar);
            fixture.Server.Sync("1", Page("2"));
            fixture.Server.Expect("Sync", document =>
            {
                var data = document.Descendants(Sync + "ApplicationData").Single();
                Check(data.Element(Calendar + "UID")?.Value == request.PreparedItem.Id.ToString("N"), "Provider maps prepared local ID to Calendar UID");
                var response = Page("3", Command("Add", "provider-id", data));
                response.Add(new XElement(Sync + "Responses", new XElement(Sync + "Add", new XElement(Sync + "Status", "1"), new XElement(Sync + "ServerId", "provider-id"))));
                return response;
            });
            await provider.ExecuteNativeRequestsAsync(provider.CreateCalendarEvent(request));
            var saved = await fixture.Calendars.GetCalendarItemAsync(request.PreparedItem.Id);
            Check(await fixture.CountAsync<CalendarItem>() == 1 && saved.RemoteEventId.GetClientTrackingId() == request.PreparedItem.Id,
                "Provider request commits client tracking before same-response echo");
            saved.Title = "Updated through provider";
            fixture.Server.Sync("3", Page("4"));
            fixture.Server.Expect("Sync", document =>
            {
                Check(document.Descendants(Sync + "Change").Single().Element(Sync + "ServerId")?.Value == "provider-id", "Provider strips client tracking from update wire ID");
                return Page("5");
            });
            await provider.ExecuteNativeRequestsAsync(provider.UpdateCalendarEvent(new UpdateCalendarEventRequest(saved, new())));
            Check((await fixture.Calendars.GetCalendarItemAsync(saved.Id))?.Title == saved.Title, "Provider update persists the changed event");
            fixture.Server.Sync("5", Page("6"));
            fixture.Server.Expect("Sync", document =>
            {
                Check(document.Descendants(Sync + "Delete").Single().Element(Sync + "ServerId")?.Value == "provider-id", "Provider strips client tracking from delete wire ID");
                return Page("7");
            });
            await provider.ExecuteNativeRequestsAsync(provider.DeleteCalendarEvent(new DeleteCalendarEventRequest(saved)));
            Check(await fixture.CountAsync<CalendarItem>() == 0, "Provider delete removes the tracked local event");
            fixture.Server.AssertDrained();
        }
        finally { await provider.KillSynchronizerAsync(); }
    }

    private static async Task LocalDraftCompletionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.AddCollectionAsync("drafts", 3);
        var draft = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = "local-draft",
            IsDraft = true,
            FolderId = EasDataMapper.StableId(state.Id),
            AssignedAccount = fixture.Account
        };
        await fixture.Database.Connection.InsertAsync(draft, typeof(MailCopy));
        var registry = new DraftUpdateRegistry();
        // A remote manager would fail this local-only path if the coordinator called it.
        await using var coordinator = new DraftUpdateCoordinator(null, fixture.Mail, null, registry);
        for (var revision = 0; revision < 2; revision++)
        {
            var snapshot = new DraftUpdateSnapshot(fixture.Account.Id, draft.UniqueId, System.Text.Encoding.UTF8.GetBytes("Subject: Draft\r\n\r\nLocal body"));
            registry.Protect(fixture.Account.Id, draft, snapshot.Revision);
            coordinator.Schedule(snapshot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (registry.IsProtected(fixture.Account.Id, draft.UniqueId))
                await Task.Delay(10, timeout.Token);
            Check(!registry.IsProtected(fixture.Account.Id, draft.UniqueId), "EAS local draft save completes without waiting for a remote mapping");
        }
        Check(await fixture.CountAsync<MailCopy>() == 1 && fixture.Server.Requests == 0, "Repeated local draft saves retain the draft without EAS requests");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal DatabaseService Database { get; }
        internal MailAccount Account { get; } = new()
        {
            Id = Guid.NewGuid(),
            Address = "user@example.test",
            Name = "EAS test",
            ProviderType = MailProviderType.EAS,
            IsMailAccessGranted = false,
            IsCalendarAccessGranted = true,
            IsContactAccessGranted = true,
            IsTaskAccessGranted = true,
            AccountColorHex = "",
            InitialSynchronizationRange = InitialSynchronizationRange.Everything,
            SynchronizationDeltaIdentifier = "folders",
            ServerInformation = new CustomServerInformation
            {
                IncomingServer = "https://eas.example.test",
                IncomingServerUsername = "user@example.test",
                IncomingServerPassword = "synthetic-test-password"
            }
        };
        internal ScriptedServer Server { get; } = new();
        internal CalendarService Calendars { get; }
        internal ContactService Contacts { get; }
        internal TaskService Tasks { get; }
        internal MimeFileService Mime { get; }
        internal DefaultChangeProcessor Processor { get; }
        internal MailService Mail => _mail;
        internal EasSynchronizationEngine Engine { get; private set; }
        private readonly AccountService _accounts;
        private readonly FolderService _folders;
        private readonly MailService _mail;
        private readonly PictureStorageService _pictures;
        private readonly TestConfiguration _configuration = new();

        private Fixture()
        {
            Database = new DatabaseService(_configuration);
            Calendars = new CalendarService(Database);
            Tasks = new TaskService(Database);
            _pictures = new PictureStorageService(_configuration, null);
            Contacts = new ContactService(Database, _pictures);
            Mime = new MimeFileService(_configuration, new UnavailableSmimeCertificateService());
            // These services use only database and local-file operations in this fixture.
            _accounts = new AccountService(Database, null, null, Mime, null, _pictures, null);
            var categories = new MailCategoryService(Database);
            _folders = new FolderService(Database, _accounts, categories);
            _mail = new MailService(Database, _folders, Contacts, _accounts, null, Mime, null,
                new SentMailReceiptService(Database, _folders, _accounts), categories);
            Processor = new DefaultChangeProcessor(Database, _folders, _mail, Calendars, _accounts, Mime, Contacts, Tasks);
            Engine = NewEngine();
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Database.InitializeAsync();
            await fixture.Database.Connection.InsertAsync(fixture.Account, typeof(MailAccount));
            await fixture.Database.Connection.InsertAsync(new MailAccountPreferences { Id = Guid.NewGuid(), AccountId = fixture.Account.Id }, typeof(MailAccountPreferences));
            return fixture;
        }

        private EasSynchronizationEngine NewEngine() => new(Account, Database, _accounts, _folders, _mail, Calendars, Contacts, Tasks, _pictures,
            new EasClient("https://eas.example.test", "user@example.test", "synthetic-test-password", Account.Id, Server));

        internal EasSynchronizer CreateProvider() => new(Account, Database, _accounts, _folders, _mail, Calendars, Contacts, Tasks, Mime, _pictures,
            new ImapChangeProcessor(Database, _folders, _mail, _accounts, Calendars, Mime, null, Contacts, Tasks, _configuration), _configuration.ApplicationDataFolderPath,
            new EasClient("https://eas.example.test", "user@example.test", "synthetic-test-password", Account.Id, Server));

        internal void RestartEngine()
        {
            Engine.Dispose();
            Engine = NewEngine();
        }

        internal async Task<EasCollectionState> AddCollectionAsync(string remoteId, int folderType)
        {
            var state = new EasCollectionState
            {
                Id = EasDataMapper.Key(Account.Id, remoteId, string.Empty),
                AccountId = Account.Id,
                RemoteId = remoteId,
                DisplayName = remoteId,
                FolderType = folderType,
                SyncKey = "1"
            };
            await Database.Connection.InsertAsync(state, typeof(EasCollectionState));
            if (folderType is 8 or 13)
                await Calendars.InsertAccountCalendarAsync(new AccountCalendar
                {
                    Id = EasDataMapper.StableId(state.Id),
                    AccountId = Account.Id,
                    RemoteCalendarId = remoteId,
                    Name = remoteId,
                    MailAccount = Account,
                    TimeZone = "UTC",
                    BackgroundColorHex = "#112233"
                });
            if (folderType is 2 or 3)
                await _folders.InsertFolderAsync(new MailItemFolder
                {
                    Id = EasDataMapper.StableId(state.Id),
                    MailAccountId = Account.Id,
                    RemoteFolderId = remoteId,
                    FolderName = remoteId,
                    SpecialFolderType = SpecialFolderType.Inbox,
                    IsSynchronizationEnabled = true
                });
            return state;
        }

        internal Task<int> CountAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() where T : new() => Database.Connection.Table<T>().CountAsync();
        internal async Task<string> StoredKeyAsync(EasCollectionState state) => (await Database.Connection.GetAsync<EasCollectionState>(state.Id)).SyncKey;

        public async ValueTask DisposeAsync()
        {
            Engine.Dispose();
            await Database.Connection.CloseAsync();
            Directory.Delete(_configuration.ApplicationDataFolderPath, true);
        }
    }

    private sealed class TestConfiguration : IApplicationConfiguration
    {
        public string ApplicationDataFolderPath { get; set; } = Path.Combine(Path.GetTempPath(), "WinoEasTests", Guid.NewGuid().ToString("N"));
        public string PublisherSharedFolderPath { get; set; } = string.Empty;
        public string ApplicationTempFolderPath { get; set; } = Path.GetTempPath();
        public string SentryDNS => string.Empty;
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        private readonly Queue<(string Command, Func<XElement, XElement> Respond)> _steps = new();
        internal int Requests { get; private set; }
        internal int Mutations { get; private set; }
        internal void Expect(string command, Func<XElement, XElement> respond) => _steps.Enqueue((command, respond));
        internal void Folders() => Expect("FolderSync", _ => new XElement(Folder + "FolderSync", new XElement(Folder + "Status", "1"),
            new XElement(Folder + "SyncKey", "folders"), new XElement(Folder + "Changes", new XElement(Folder + "Count", "0"))));
        internal void Sync(string expectedKey, XElement response) => Expect("Sync", request =>
        {
            Check(request.Descendants(SynchronizationTests.Sync + "Collection").Single().Element(SynchronizationTests.Sync + "SyncKey")?.Value == expectedKey,
                $"Sync request uses the last successfully persisted key: expected {expectedKey}, got {request.Descendants(SynchronizationTests.Sync + "SyncKey").FirstOrDefault()?.Value}");
            return response;
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Options)
            {
                var options = new HttpResponseMessage(HttpStatusCode.OK);
                options.Headers.Add("MS-ASProtocolVersions", "12.0,14.1");
                return options;
            }
            var step = _steps.Dequeue();
            Check(request.RequestUri?.Query.Contains("Cmd=" + step.Command, StringComparison.Ordinal) == true, "Expected EAS HTTP command");
            var content = request.Content ?? throw new InvalidOperationException("Missing EAS request body");
            var document = EasWbxml.Decode(await content.ReadAsByteArrayAsync(cancellationToken));
            if (document.Descendants(SynchronizationTests.Sync + "Commands").Any()) Mutations++;
            var response = step.Respond(document);
            if (response.Name == SynchronizationTests.Sync + "Collection")
                response = new XElement(SynchronizationTests.Sync + "Sync", new XElement(SynchronizationTests.Sync + "Collections", response));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(EasWbxml.Encode(response)) };
        }

        internal void AssertDrained() => Check(_steps.Count == 0, "All expected server exchanges completed");
        // Restarting a client must keep this scripted server's remaining exchanges available.
        protected override void Dispose(bool disposing) { }
    }
}
