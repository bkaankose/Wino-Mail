using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Synchronizers.Exchange;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Exchange;

/// <summary>
/// A transport change (EWS to MAPI or back) addresses the same Contacts and Tasks folders under new
/// ids. The address book, the task list and the rows in them must survive it.
/// </summary>
public class ExchangeTransportRekeyTests : IAsyncLifetime
{
    private const string EwsFolderId = "AAMkAGFolder=";
    private const string MapiFolderId = "mapi:00000000000A0001";

    private InMemoryDatabaseService _database = null!;
    private ContactService _contactService = null!;
    private TaskService _taskService = null!;
    private Guid _accountId;

    public async Task InitializeAsync()
    {
        _database = new InMemoryDatabaseService();
        await _database.InitializeAsync();
        _contactService = new ContactService(_database);
        _taskService = new TaskService(_database);
        _accountId = Guid.NewGuid();
        await _database.Connection.InsertAsync(
            new MailAccount { Id = _accountId, Name = "Exchange", ProviderType = MailProviderType.Exchange },
            typeof(MailAccount));
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public void IsOtherTransport_ComparesTheIdSchemes()
    {
        ExchangeTransportRekey.IsOtherTransport("AAMkAG=", "mapi:0000000000000001").Should().BeTrue();
        ExchangeTransportRekey.IsOtherTransport("mapi:0000000000000001", "AAMkAG=").Should().BeTrue();
        ExchangeTransportRekey.IsOtherTransport("AAMkAG=", "AAMkAH=").Should().BeFalse();
        ExchangeTransportRekey.IsOtherTransport("mapi:01", "mapi:02").Should().BeFalse();
        ExchangeTransportRekey.IsOtherTransport(null, "mapi:02").Should().BeFalse();
    }

    [Fact]
    public void MatchContacts_PairsUniqueContentAcrossTransportsOnly()
    {
        var alice = Contact("ews-alice", "Alice", "alice@example.test");
        var firstTwin = Contact("ews-twin-1", "Twin", "twin@example.test");
        var secondTwin = Contact("ews-twin-2", "Twin", "twin@example.test");
        var pending = Contact("ews-pending", "Pending", "pending@example.test");
        pending.PendingMutation = ContactPendingMutation.Update;

        var matches = ExchangeTransportRekey.MatchContacts(
            [alice, firstTwin, secondTwin, pending],
            [
                Contact("mapi:01", "ALICE", "Alice@Example.test"),
                Contact("mapi:02", "Twin", "twin@example.test"),
                Contact("mapi:03", "Pending", "pending@example.test"),
                Contact("mapi:04", "Newcomer", "new@example.test")
            ]);

        matches.Should().BeEquivalentTo(new Dictionary<Guid, string> { [alice.Id] = "mapi:01" });
    }

    [Fact]
    public void MatchContacts_LeavesTheSameTransportAlone()
    {
        // A contact deleted and recreated on the server is a new contact, not a re-keyed one.
        var matches = ExchangeTransportRekey.MatchContacts(
            [Contact("mapi:01", "Alice", "alice@example.test")],
            [Contact("mapi:09", "Alice", "alice@example.test")]);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void MatchTasks_PairsByTitleAndDueDate()
    {
        var report = NewTask("ews-report", "Quarterly report", new DateTime(2026, 9, 30));
        var call = NewTask("ews-call", "Call back", null);

        var matches = ExchangeTransportRekey.MatchTasks(
            [report, call],
            [
                NewTask("mapi:11", "Quarterly report", new DateTime(2026, 9, 30)),
                NewTask("mapi:12", "Call back", new DateTime(2026, 10, 1))
            ]);

        matches.Should().BeEquivalentTo(new Dictionary<Guid, string> { [report.Id] = "mapi:11" });
    }

    [Fact]
    public async Task RekeyedAddressBook_KeepsTheBookTheContactAndItsFavourite()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Exchange, EwsFolderId, "Exchange", true);
        var stored = Contact("ews-alice", "Alice", "alice@example.test");
        await _contactService.ReplaceAddressBookAsync(book.Id, [stored], null);
        var storedId = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single().Id;
        await _contactService.SetContactFavoriteAsync(storedId, true);

        // What the synchronizer does on the first pass over the other transport.
        var incoming = new List<AccountContact> { Contact("mapi:01", "Alice", "alice@example.test") };
        await _contactService.RekeyAddressBookAsync(book.Id, MapiFolderId, new Dictionary<Guid, string>());
        var matches = ExchangeTransportRekey.MatchContacts(await _contactService.GetContactsByAddressBookAsync(book.Id), incoming);
        await _contactService.RekeyAddressBookAsync(book.Id, MapiFolderId, matches);
        var reopened = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Exchange, MapiFolderId, "Exchange", true);
        await _contactService.ReplaceAddressBookAsync(reopened.Id, incoming, null);

        reopened.Id.Should().Be(book.Id);
        (await _contactService.GetAddressBooksAsync(_accountId)).Where(b => b.SourceKind == ContactSourceKind.Exchange).Should().ContainSingle();
        var contact = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        contact.Id.Should().Be(storedId);
        contact.RemoteId.Should().Be("mapi:01");
        contact.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task RekeyedTaskList_KeepsTheListItsColourAndTheTask()
    {
        await ApplyTopologyAsync(EwsFolderId);
        var list = (await _taskService.GetTaskListsAsync(_accountId)).Single(l => l.SourceKind == TaskSourceKind.Exchange);
        await _database.Connection.ExecuteAsync("UPDATE TaskList SET ColorHex = ? WHERE Id = ?", "#123456", list.Id);
        await ApplySnapshotAsync(list.Id, NewTask("ews-report", "Quarterly report", new DateTime(2026, 9, 30)));
        var storedTask = (await _taskService.GetTasksAsync(listId: list.Id)).Single();

        var incoming = NewTask("mapi:11", "Quarterly report", new DateTime(2026, 9, 30));
        await _taskService.RekeyTaskListAsync(list.Id, MapiFolderId, new Dictionary<Guid, string>());
        await ApplyTopologyAsync(MapiFolderId);
        var matches = ExchangeTransportRekey.MatchTasks(await _taskService.GetTasksAsync(listId: list.Id), [incoming]);
        await _taskService.RekeyTaskListAsync(list.Id, MapiFolderId, matches);
        await ApplySnapshotAsync(list.Id, incoming);

        var lists = (await _taskService.GetTaskListsAsync(_accountId)).Where(l => l.SourceKind == TaskSourceKind.Exchange).ToList();
        lists.Should().ContainSingle();
        lists[0].Id.Should().Be(list.Id);
        lists[0].RemoteId.Should().Be(MapiFolderId);
        lists[0].ColorHex.Should().Be("#123456");
        lists[0].PendingMutation.Should().Be(TaskPendingMutation.None);
        var task = (await _taskService.GetTasksAsync(listId: list.Id)).Single();
        task.Id.Should().Be(storedTask.Id);
        task.RemoteId.Should().Be("mapi:11");
        task.PendingMutation.Should().Be(TaskPendingMutation.None);
    }

    private Task ApplyTopologyAsync(string remoteFolderId)
        => _taskService.ApplyTaskTopologyDeltaAsync(new TaskTopologyDelta
        {
            MailAccountId = _accountId,
            SourceKind = TaskSourceKind.Exchange,
            Lists =
            [
                new AccountTaskList
                {
                    MailAccountId = _accountId,
                    SourceKind = TaskSourceKind.Exchange,
                    RemoteId = remoteFolderId,
                    Title = "Tasks",
                    IsDefault = true
                }
            ],
            ReconcileLists = true
        });

    private Task ApplySnapshotAsync(Guid listId, AccountTask task)
        => _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = listId,
            Tasks = [task],
            AuthoritativeStepParentRemoteIds = [task.RemoteId],
            IsFullSnapshot = true,
            WatermarkUtc = DateTime.UtcNow
        });

    private AccountContact Contact(string remoteId, string name, string address)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = _accountId,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = remoteId,
            DisplayName = name,
            Address = address
        };

    private AccountTask NewTask(string remoteId, string title, DateTime? dueDate)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = _accountId,
            SourceKind = TaskSourceKind.Exchange,
            RemoteId = remoteId,
            Title = title,
            DueDate = dueDate
        };
}
