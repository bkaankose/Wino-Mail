using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Helpers;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Tasks;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ContactTaskRequestStateTests
{
    [Fact]
    public void ContactRequest_UsesDeepSnapshotsForApplyAndExactRevert()
    {
        WeakReferenceMessenger.Default.Reset();
        var recipient = new ContactRecipient();
        WeakReferenceMessenger.Default.RegisterAll(recipient);
        var original = CreateContact("Original", "old@example.com");
        var desired = CreateContact("Desired", "new@example.com", original.Id);
        var request = new ContactActionRequest(desired, ContactSynchronizerOperation.Update, original);

        desired.DisplayName = "Mutated after preparation";
        desired.EmailAddresses[0].Address = "mutated@example.com";
        original.DisplayName = "Mutated original";

        try
        {
            request.ApplyUIChanges();
            request.RevertUIChanges();

            recipient.Messages.Select(message => message.Contact.DisplayName)
                .Should().Equal("Desired", "Original");
            recipient.Messages[0].Contact.EmailAddresses[0].Address.Should().Be("new@example.com");
            recipient.Messages[1].Source.Should().Be(EntityUpdateSource.ClientReverted);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            WeakReferenceMessenger.Default.Reset();
        }
    }

    [Fact]
    public void TaskRequest_ApplyRevertAndCompletionReleaseCoordinatorState()
    {
        WeakReferenceMessenger.Default.Reset();
        var recipient = new TaskRecipient();
        WeakReferenceMessenger.Default.RegisterAll(recipient);
        var accountId = Guid.NewGuid();
        var original = new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            TaskListId = Guid.NewGuid(),
            Title = "Original",
            Steps = [new AccountTaskStep { Id = Guid.NewGuid(), Title = "Step" }]
        };
        var desired = Wino.Core.Requests.RequestEntityCloner.Task(original);
        desired.Title = "Desired";
        desired.Steps[0].Title = "Desired step";
        var request = new TaskActionRequest(
            accountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original);

        desired.Title = "Mutated after preparation";
        desired.Steps[0].Title = "Mutated step";

        try
        {
            RequestUiChangeCoordinator.ApplyRequests([request]);
            RequestUiChangeCoordinator.RevertRequests([request]);
            RequestUiChangeCoordinator.CompleteRequests([request]);
            RequestUiChangeCoordinator.ApplyRequests([request]);

            recipient.Messages.Select(message => message.Task.Title)
                .Should().Equal("Desired", "Original", "Desired");
            recipient.Messages[0].Task.Steps[0].Title.Should().Be("Desired step");
        }
        finally
        {
            RequestUiChangeCoordinator.CompleteRequests([request]);
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            WeakReferenceMessenger.Default.Reset();
        }
    }

    private static AccountContact CreateContact(string displayName, string email, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            MailAccountId = Guid.NewGuid(),
            AddressBookId = Guid.NewGuid(),
            DisplayName = displayName,
            EmailAddresses =
            [
                new ContactEmailAddress
                {
                    Id = Guid.NewGuid(),
                    Address = email
                }
            ]
        };

    public sealed class ContactRecipient : IRecipient<ContactStateChanged>
    {
        public List<ContactStateChanged> Messages { get; } = [];
        public void Receive(ContactStateChanged message) => Messages.Add(message);
    }

    public sealed class TaskRecipient : IRecipient<TaskStateChanged>
    {
        public List<TaskStateChanged> Messages { get; } = [];
        public void Receive(TaskStateChanged message) => Messages.Add(message);
    }
}
