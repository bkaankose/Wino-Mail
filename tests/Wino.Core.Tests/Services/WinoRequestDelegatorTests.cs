using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Tasks;
using Wino.Core.Services;
using Wino.Messaging.Server;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoRequestDelegatorTests
{
    [Fact]
    public async Task ExecuteAsync_TaskRequest_QueuesOnceAndPublishesTaskSynchronization()
    {
        var accountId = Guid.NewGuid();
        var synchronizationManager = CreateSynchronizationManager();
        var delegator = CreateDelegator(synchronizationManager.Object);
        var recorder = new SynchronizationRequestRecorder();
        WeakReferenceMessenger.Default.RegisterAll(recorder);

        try
        {
            await delegator.ExecuteAsync(accountId,
            [
                new TaskActionRequest(accountId, TaskSynchronizerOperation.CreateTask, Task: new AccountTask
                {
                    MailAccountId = accountId,
                    TaskListId = Guid.NewGuid(),
                    Title = "Queued task"
                })
            ]);

            synchronizationManager.Verify(manager => manager.QueueRequestPackAsync(
                It.Is<IReadOnlyDictionary<Guid, List<IRequestBase>>>(pack =>
                    pack.Count == 1 && pack.ContainsKey(accountId) && pack[accountId].Count == 1),
                false), Times.Once);
            synchronizationManager.Verify(manager => manager.SynchronizeTasksAsync(
                It.IsAny<Wino.Core.Domain.Models.Synchronization.TaskSynchronizationOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);
            recorder.Tasks.Should().ContainSingle().Which.Options.Should().Match<Wino.Core.Domain.Models.Synchronization.TaskSynchronizationOptions>(options =>
                options.AccountId == accountId && options.Type == TaskSynchronizationType.ExecuteRequests);
            recorder.Mail.Should().BeEmpty();
            recorder.Calendar.Should().BeEmpty();
            recorder.Contacts.Should().BeEmpty();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recorder);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MixedRequests_PublishesOneIsolatedRequestPerMode()
    {
        var accountId = Guid.NewGuid();
        var addressBookId = Guid.NewGuid();
        var synchronizationManager = CreateSynchronizationManager();
        var delegator = CreateDelegator(synchronizationManager.Object);
        var recorder = new SynchronizationRequestRecorder();
        WeakReferenceMessenger.Default.RegisterAll(recorder);

        try
        {
            await delegator.ExecuteAsync(accountId,
            [
                new TestMailRequest(),
                new TestCalendarRequest(),
                new TestContactRequest(accountId, addressBookId),
                new TaskActionRequest(accountId, TaskSynchronizerOperation.UpdateTask)
            ]);

            recorder.Mail.Should().ContainSingle().Which.Options.Type.Should().Be(MailSynchronizationType.ExecuteRequests);
            recorder.Calendar.Should().ContainSingle().Which.Options.Type.Should().Be(CalendarSynchronizationType.ExecuteRequests);
            recorder.Contacts.Should().ContainSingle().Which.Options.Should().Match<Wino.Core.Domain.Models.Synchronization.ContactSynchronizationOptions>(options =>
                options.AccountId == accountId && options.AddressBookId == addressBookId && options.Type == ContactSynchronizationType.ExecuteRequests);
            recorder.Tasks.Should().ContainSingle().Which.Options.Type.Should().Be(TaskSynchronizationType.ExecuteRequests);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recorder);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteFolder_PublishesExecutionAndFolderRefresh()
    {
        var accountId = Guid.NewGuid();
        var synchronizationManager = CreateSynchronizationManager();
        var delegator = CreateDelegator(synchronizationManager.Object);
        var recorder = new SynchronizationRequestRecorder();
        WeakReferenceMessenger.Default.RegisterAll(recorder);

        try
        {
            await delegator.ExecuteAsync(accountId, [new DeleteFolderRequest(new MailItemFolder { MailAccountId = accountId })]);

            recorder.Mail.Select(message => message.Options.Type)
                .Should().Equal(MailSynchronizationType.ExecuteRequests, MailSynchronizationType.FoldersOnly);
            recorder.Calendar.Should().BeEmpty();
            recorder.Contacts.Should().BeEmpty();
            recorder.Tasks.Should().BeEmpty();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recorder);
        }
    }

    private static WinoRequestDelegator CreateDelegator(ISynchronizationManager synchronizationManager)
        => new(
            Mock.Of<IWinoRequestProcessor>(),
            Mock.Of<IFolderService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<ICalendarService>(),
            Mock.Of<IContactService>(),
            synchronizationManager,
            Mock.Of<IImapChangeProcessor>(),
            Mock.Of<IApplicationConfiguration>());

    private static Mock<ISynchronizationManager> CreateSynchronizationManager()
    {
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(service => service.QueueRequestPackAsync(
                It.IsAny<IReadOnlyDictionary<Guid, List<IRequestBase>>>(),
                It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        return manager;
    }

    private abstract class TestRequest : IRequestBase
    {
        public int ResynchronizationDelay => 0;
        public object GroupingKey() => GetType();
        public void ApplyUIChanges() { }
        public void RevertUIChanges() { }
    }

    private sealed class TestMailRequest : TestRequest, IMailActionRequest
    {
        public MailCopy Item => null;
        public MailSynchronizerOperation Operation => MailSynchronizerOperation.MarkRead;
    }

    private sealed class TestCalendarRequest : TestRequest, ICalendarActionRequest
    {
        public CalendarItem Item => null;
        public Guid? LocalCalendarItemId => null;
        public CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.UpdateEvent;
    }

    private sealed class TestContactRequest(Guid accountId, Guid addressBookId) : TestRequest, IContactActionRequest
    {
        public Guid LocalContactId { get; } = Guid.NewGuid();
        public Guid MailAccountId { get; } = accountId;
        public Guid AddressBookId { get; } = addressBookId;
        public ContactSourceKind SourceKind => ContactSourceKind.Local;
        public ContactSynchronizerOperation Operation => ContactSynchronizerOperation.Update;
        public byte[] Photo => null;
    }

    internal sealed class SynchronizationRequestRecorder :
        IRecipient<NewMailSynchronizationRequested>,
        IRecipient<NewCalendarSynchronizationRequested>,
        IRecipient<NewContactSynchronizationRequested>,
        IRecipient<NewTaskSynchronizationRequested>
    {
        public List<NewMailSynchronizationRequested> Mail { get; } = [];
        public List<NewCalendarSynchronizationRequested> Calendar { get; } = [];
        public List<NewContactSynchronizationRequested> Contacts { get; } = [];
        public List<NewTaskSynchronizationRequested> Tasks { get; } = [];

        public void Receive(NewMailSynchronizationRequested message) => Mail.Add(message);
        public void Receive(NewCalendarSynchronizationRequested message) => Calendar.Add(message);
        public void Receive(NewContactSynchronizationRequested message) => Contacts.Add(message);
        public void Receive(NewTaskSynchronizationRequested message) => Tasks.Add(message);
    }
}
