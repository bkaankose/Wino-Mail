using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Exchange.Streaming;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Push changes from an Exchange mailbox land on the sync the affected folder belongs to: mail folders,
/// calendars, the Contacts folder and the Tasks folder, all keyed by "mapi:" + folder id on the MAPI
/// transport, with an unknown folder falling back to a hierarchy refresh.
/// </summary>
public sealed class StreamingEventRouterTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private const string InboxMapiId = "0000000000000001";
    private const string CalendarMapiId = "0000000000000002";
    private const string ContactsMapiId = "0000000000000003";
    private const string TasksMapiId = "0000000000000004";
    private const string UnknownMapiId = "00000000000000FF";

    private static readonly MailItemFolder Inbox = new() { Id = Guid.NewGuid(), MailAccountId = AccountId, MapiFolderId = InboxMapiId, RemoteFolderId = "mapi:" + InboxMapiId };
    private static readonly AccountCalendar Calendar = new() { Id = Guid.NewGuid(), AccountId = AccountId, RemoteCalendarId = "mapi:" + CalendarMapiId };

    private static StreamingEventRouter CreateRouter(bool withContactsAndTasks = true)
    {
        var folders = new Mock<IFolderService>();
        folders.Setup(f => f.GetFolderByMapiIdAsync(AccountId, InboxMapiId)).ReturnsAsync(Inbox);
        folders.Setup(f => f.GetFolderByMapiIdAsync(AccountId, It.IsNotIn(InboxMapiId))).ReturnsAsync((MailItemFolder)null);

        var calendars = new Mock<ICalendarService>();
        calendars.Setup(c => c.GetAccountCalendarsAsync(AccountId)).ReturnsAsync([Calendar]);

        var contacts = new Mock<IContactService>();
        contacts.Setup(c => c.GetAddressBooksAsync(AccountId)).ReturnsAsync(
        [
            new ContactAddressBook { Id = Guid.NewGuid(), MailAccountId = AccountId, SourceKind = ContactSourceKind.Exchange, RemoteId = "mapi:" + ContactsMapiId }
        ]);

        var tasks = new Mock<ITaskService>();
        tasks.Setup(t => t.GetTaskListsAsync(AccountId)).ReturnsAsync(
        [
            new AccountTaskList { Id = Guid.NewGuid(), MailAccountId = AccountId, SourceKind = TaskSourceKind.Exchange, RemoteId = "mapi:" + TasksMapiId }
        ]);

        return withContactsAndTasks
            ? new StreamingEventRouter(folders.Object, calendars.Object, contacts.Object, tasks.Object)
            : new StreamingEventRouter(folders.Object, calendars.Object);
    }

    [Fact]
    public async Task MailFolderChange_SyncsThatFolderOnly()
    {
        var result = await CreateRouter().RouteAsync(AccountId, [new StreamingChange(null, false, InboxMapiId)]);

        var mailSync = result.MailSyncs.Should().ContainSingle().Subject;
        mailSync.Options.Type.Should().Be(MailSynchronizationType.CustomFolders);
        mailSync.Options.SynchronizationFolderIds.Should().Equal(Inbox.Id);
        result.CalendarSyncs.Should().BeEmpty();
        result.ContactSyncs.Should().BeEmpty();
        result.TaskSyncs.Should().BeEmpty();
    }

    [Fact]
    public async Task CalendarFolderChange_SyncsThatCalendar()
    {
        var result = await CreateRouter().RouteAsync(AccountId, [new StreamingChange(null, false, CalendarMapiId)]);

        result.MailSyncs.Should().BeEmpty();
        var calendarSync = result.CalendarSyncs.Should().ContainSingle().Subject;
        calendarSync.Options.Type.Should().Be(CalendarSynchronizationType.SingleCalendar);
        calendarSync.Options.SynchronizationCalendarIds.Should().Equal(Calendar.Id);
    }

    [Fact]
    public async Task ContactsAndTasksFolderChanges_SyncContactsAndTasks()
    {
        var result = await CreateRouter().RouteAsync(AccountId,
        [
            new StreamingChange(null, false, ContactsMapiId),
            new StreamingChange(null, false, TasksMapiId)
        ]);

        result.MailSyncs.Should().BeEmpty("neither folder is in the mail tree, and both are known");
        result.ContactSyncs.Should().ContainSingle().Which.Options.Type.Should().Be(ContactSynchronizationType.Full);
        result.TaskSyncs.Should().ContainSingle().Which.Options.Type.Should().Be(TaskSynchronizationType.Full);
        result.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task ContactsFolderChange_WithoutContactService_RefreshesTheHierarchy()
    {
        var result = await CreateRouter(withContactsAndTasks: false).RouteAsync(AccountId, [new StreamingChange(null, false, ContactsMapiId)]);

        result.ContactSyncs.Should().BeEmpty();
        result.MailSyncs.Should().ContainSingle().Which.Options.Type.Should().Be(MailSynchronizationType.FoldersOnly);
    }

    [Fact]
    public async Task UnknownFolderAndHierarchyEvents_RefreshTheHierarchyOnce()
    {
        var result = await CreateRouter().RouteAsync(AccountId,
        [
            new StreamingChange(null, true),
            new StreamingChange(null, false, UnknownMapiId)
        ]);

        result.MailSyncs.Should().ContainSingle().Which.Options.Type.Should().Be(MailSynchronizationType.FoldersOnly);
        result.CalendarSyncs.Should().BeEmpty();
    }

    [Fact]
    public async Task NoChanges_RouteToNothing()
    {
        (await CreateRouter().RouteAsync(AccountId, [])).IsEmpty.Should().BeTrue();
    }
}
