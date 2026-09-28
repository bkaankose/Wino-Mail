using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Contact;
using Wino.Core.Synchronizers;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class WinoSynchronizerMailRequestTests
{
    [Fact]
    public async Task FoldersOnly_sync_should_not_execute_queued_folder_requests()
    {
        var synchronizer = new TestMailSynchronizer();
        var request = new CreateRootFolderRequest(
            new MailItemFolder { Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id },
            "test");

        synchronizer.QueueRequest(request);

        var foldersOnlyResult = await synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = synchronizer.Account.Id,
            Type = MailSynchronizationType.FoldersOnly
        });

        foldersOnlyResult.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.CreateRootFolderInvocationCount.Should().Be(0);
        synchronizer.ExecuteNativeRequestsInvocationCount.Should().Be(0);

        var executeRequestsResult = await synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = synchronizer.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        executeRequestsResult.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.CreateRootFolderInvocationCount.Should().Be(1);
        synchronizer.ExecuteNativeRequestsInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteRequests_should_dispatch_grouped_mark_read_requests_with_composite_grouping_key()
    {
        var synchronizer = new TestMailSynchronizer();
        var folderId = Guid.NewGuid();

        synchronizer.QueueRequest(new MarkReadRequest(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = folderId
        }, IsRead: true));

        synchronizer.QueueRequest(new MarkReadRequest(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = folderId
        }, IsRead: true));

        var result = await synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = synchronizer.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.MarkReadInvocationCount.Should().Be(1);
        synchronizer.LastMarkReadBatchCount.Should().Be(2);
        synchronizer.ExecuteNativeRequestsInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteRequests_should_create_a_native_bundle_for_each_local_draft()
    {
        var synchronizer = new TestMailSynchronizer();
        var draftFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = synchronizer.Account.Id,
            RemoteFolderId = "drafts"
        };

        synchronizer.QueueRequest(CreateDraftRequest());
        synchronizer.QueueRequest(CreateDraftRequest());

        var result = await synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = synchronizer.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.CreateDraftInvocationCount.Should().Be(2);
        synchronizer.LastNativeRequestCount.Should().Be(2);

        CreateDraftRequest CreateDraftRequest()
        {
            var copy = new MailCopy
            {
                UniqueId = Guid.NewGuid(),
                Id = Guid.NewGuid().ToString(),
                FolderId = draftFolder.Id,
                DraftId = $"localDraft_{Guid.NewGuid()}",
                AssignedFolder = draftFolder,
                AssignedAccount = synchronizer.Account
            };

            return new CreateDraftRequest(new DraftPreparationRequest(
                synchronizer.Account,
                copy,
                string.Empty,
                DraftCreationReason.Empty));
        }
    }

    [Fact]
    public async Task Mail_execution_keeps_contact_requests_in_the_shared_queue()
    {
        var synchronizer = new TestMailSynchronizer();
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id,
            AddressBookId = Guid.NewGuid(), SourceKind = ContactSourceKind.Local
        };
        synchronizer.QueueRequest(new ContactActionRequest(contact, ContactSynchronizerOperation.Update));
        synchronizer.QueueRequest(new MarkReadRequest(new MailCopy { UniqueId = Guid.NewGuid(), Id = "mail", FolderId = Guid.NewGuid() }, true));

        await synchronizer.SynchronizeMailsAsync(new() { AccountId = synchronizer.Account.Id, Type = MailSynchronizationType.ExecuteRequests });

        synchronizer.HasPendingContactOperation(contact.Id).Should().BeTrue();
        synchronizer.ContactRequestInvocationCount.Should().Be(0);

        await synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });

        synchronizer.ContactRequestInvocationCount.Should().Be(1);
        synchronizer.HasPendingContactOperation(contact.Id).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteRequests_contact_sync_should_not_trigger_a_provider_resynchronization()
    {
        var synchronizer = new TestMailSynchronizer();
        var contact = new AccountContact { Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id, AddressBookId = Guid.NewGuid(), SourceKind = ContactSourceKind.Outlook };
        synchronizer.QueueRequest(new ContactActionRequest(contact, ContactSynchronizerOperation.Update));

        var result = await synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.ContactRequestInvocationCount.Should().Be(1);
        synchronizer.ContactSyncInvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Delta_contact_sync_should_still_reach_the_provider()
    {
        var synchronizer = new TestMailSynchronizer();
        await synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.Delta });
        synchronizer.ContactSyncInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Failed_contact_requests_should_be_dropped_from_the_queue()
    {
        var synchronizer = new TestMailSynchronizer { ThrowOnContactRequests = true };
        var contact = new AccountContact { Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id, AddressBookId = Guid.NewGuid(), SourceKind = ContactSourceKind.Gmail };
        synchronizer.QueueRequest(new ContactActionRequest(contact, ContactSynchronizerOperation.Create));

        var result = await synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Failed);
        synchronizer.HasPendingContactOperation(contact.Id).Should().BeFalse();
        synchronizer.ThrowOnContactRequests = false;
        await synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });
        synchronizer.ContactRequestInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Contact_requests_queued_during_execution_are_processed_once_by_the_next_run()
    {
        var synchronizer = new TestMailSynchronizer { BlockContactRequests = true };
        var first = new AccountContact { Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id, AddressBookId = Guid.NewGuid() };
        var second = new AccountContact { Id = Guid.NewGuid(), MailAccountId = synchronizer.Account.Id, AddressBookId = first.AddressBookId };
        synchronizer.QueueRequest(new ContactActionRequest(first, ContactSynchronizerOperation.Update));

        var firstRun = synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });
        await synchronizer.ContactRequestsStarted.Task;
        synchronizer.QueueRequest(new ContactActionRequest(second, ContactSynchronizerOperation.SetPhoto, Photo: [1, 2, 3]));
        var secondRun = synchronizer.SynchronizeContactsAsync(new() { AccountId = synchronizer.Account.Id, Type = ContactSynchronizationType.ExecuteRequests });

        synchronizer.ReleaseContactRequests.TrySetResult();
        await Task.WhenAll(firstRun, secondRun);

        synchronizer.ContactRequestInvocationCount.Should().Be(2);
        synchronizer.HasPendingContactOperation(first.Id).Should().BeFalse();
        synchronizer.HasPendingContactOperation(second.Id).Should().BeFalse();
    }

    [Fact]
    public async Task IgnoredIdleAndCanceledWaiterDoNotReleaseAnotherRequestsSemaphore()
    {
        var synchronizer = new TestMailSynchronizer { BlockMailSync = true };
        var active = synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.IMAPIdle });
        await synchronizer.MailSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ignored = await synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.IMAPIdle });
        ignored.CompletedState.Should().Be(SynchronizationCompletedState.Canceled);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.FoldersOnly }, canceled.Token);
        var waiting = synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.FoldersOnly });
        waiting.IsCompleted.Should().BeFalse();
        synchronizer.MailSyncInvocationCount.Should().Be(1);
        synchronizer.ReleaseMailSync.TrySetResult();
        await Task.WhenAll(active, waiting).WaitAsync(TimeSpan.FromSeconds(5));
        synchronizer.MailSyncInvocationCount.Should().Be(2);
    }

    [Fact]
    public async Task SerializedMailRequests_WaitForActiveSyncBeforeExecutingMutations()
    {
        var synchronizer = new TestMailSynchronizer { BlockMailSync = true, SerializeRequests = true };
        var active = synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.IMAPIdle });
        await synchronizer.MailSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        synchronizer.QueueRequest(new MarkReadRequest(new MailCopy { UniqueId = Guid.NewGuid(), FolderId = Guid.NewGuid() }, true));
        var waiting = synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.ExecuteRequests });
        synchronizer.ExecuteNativeRequestsInvocationCount.Should().Be(0);
        synchronizer.ReleaseMailSync.TrySetResult();
        await Task.WhenAll(active, waiting).WaitAsync(TimeSpan.FromSeconds(5));
        synchronizer.ExecuteNativeRequestsInvocationCount.Should().Be(1);
        synchronizer.HasQueuedRequests().Should().BeFalse();
    }

    [Fact]
    public async Task RequestEnqueuedDuringBatchPreparation_RemainsQueuedAndTracked()
    {
        var synchronizer = new TestMailSynchronizer { SerializeRequests = true };
        var mail = new MailCopy { UniqueId = Guid.NewGuid(), FolderId = Guid.NewGuid() };
        synchronizer.QueueRequest(new MarkReadRequest(mail, true));
        synchronizer.OnMarkRead = () => synchronizer.QueueRequest(new MarkReadRequest(mail, false));
        await synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.ExecuteRequests });
        synchronizer.HasQueuedRequests().Should().BeTrue();
        synchronizer.HasPendingOperation(mail.UniqueId).Should().BeTrue();
        synchronizer.OnMarkRead = null;
        await synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.ExecuteRequests });
        synchronizer.MarkReadInvocationCount.Should().Be(2);
        synchronizer.HasQueuedRequests().Should().BeFalse();
        synchronizer.HasPendingOperation(mail.UniqueId).Should().BeFalse();
    }

    [Fact]
    public async Task SerializedMailRequests_PreserveToggleOrder()
    {
        var synchronizer = new TestMailSynchronizer { SerializeRequests = true };
        var mail = new MailCopy { UniqueId = Guid.NewGuid(), FolderId = Guid.NewGuid() };
        synchronizer.QueueRequest(new MarkReadRequest(mail, true));
        synchronizer.QueueRequest(new MarkReadRequest(mail, false));
        synchronizer.QueueRequest(new MarkReadRequest(mail, true));
        await synchronizer.SynchronizeMailsAsync(new() { Type = MailSynchronizationType.ExecuteRequests });
        synchronizer.MarkReadStates.Should().Equal(true, false, true);
    }

    private sealed class TestMailSynchronizer
        : WinoSynchronizer<object, object, object, object>
    {
        public TestMailSynchronizer()
            : base(new MailAccount { Id = Guid.NewGuid(), Name = "Test account" }, WeakReferenceMessenger.Default)
        {
        }

        public override uint BatchModificationSize => 1;
        public override uint InitialMessageDownloadCountPerFolder => 0;
        public int CreateRootFolderInvocationCount { get; private set; }
        public int MarkReadInvocationCount { get; private set; }
        public int LastMarkReadBatchCount { get; private set; }
        public int ExecuteNativeRequestsInvocationCount { get; private set; }
        public int CreateDraftInvocationCount { get; private set; }
        public int LastNativeRequestCount { get; private set; }
        public int ContactRequestInvocationCount { get; private set; }
        public int ContactSyncInvocationCount { get; private set; }
        public bool BlockMailSync { get; set; }
        public bool SerializeRequests { get; set; }
        protected override bool SerializeMailOperations => SerializeRequests;
        public Action? OnMarkRead { get; set; }
        public List<bool> MarkReadStates { get; } = [];
        public int MailSyncInvocationCount { get; private set; }
        public TaskCompletionSource MailSyncStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMailSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOnContactRequests { get; set; }
        public bool BlockContactRequests { get; set; }
        public TaskCompletionSource ContactRequestsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseContactRequests { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        {
            ContactRequestInvocationCount += requests.Count;
            if (ThrowOnContactRequests)
                throw new InvalidOperationException("Contact request failed.");
            if (BlockContactRequests)
            {
                BlockContactRequests = false;
                ContactRequestsStarted.TrySetResult();
                await ReleaseContactRequests.Task.WaitAsync(cancellationToken);
            }
        }

        protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(
            ContactSynchronizationOptions options,
            CancellationToken cancellationToken = default)
        {
            ContactSyncInvocationCount++;
            return Task.FromResult(ContactSynchronizationResult.Empty);
        }

        public override List<IRequestBundle<object>> CreateRootFolder(CreateRootFolderRequest request)
        {
            CreateRootFolderInvocationCount++;
            return [new TestRequestBundle(new object(), request)];
        }

        public override List<IRequestBundle<object>> MarkRead(BatchMarkReadRequest request)
        {
            MarkReadInvocationCount++;
            MarkReadStates.Add(request[0].IsRead);
            OnMarkRead?.Invoke();
            LastMarkReadBatchCount = request.Count;
            return [new TestRequestBundle(new object(), request[0])];
        }

        public override List<IRequestBundle<object>> CreateDraft(CreateDraftRequest request)
        {
            CreateDraftInvocationCount++;
            return [new TestRequestBundle(new object(), request)];
        }

        public override Task ExecuteNativeRequestsAsync(List<IRequestBundle<object>> batchedRequests, CancellationToken cancellationToken = default)
        {
            ExecuteNativeRequestsInvocationCount++;
            LastNativeRequestCount = batchedRequests.Count;
            return Task.CompletedTask;
        }

        public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(
            object message,
            MailItemFolder assignedFolder,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<NewMailItemPackage>());

        protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(
            MailSynchronizationOptions options,
            CancellationToken cancellationToken = default)
        {
            MailSyncInvocationCount++;
            if (BlockMailSync)
            {
                BlockMailSync = false;
                MailSyncStarted.TrySetResult();
                await ReleaseMailSync.Task.WaitAsync(cancellationToken);
            }
            return MailSynchronizationResult.Empty;
        }

        protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(
            CalendarSynchronizationOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CalendarSynchronizationResult.Empty);
    }

    private sealed class TestRequestBundle : IRequestBundle<object>
    {
        public TestRequestBundle(object nativeRequest, IRequestBase request)
        {
            NativeRequest = nativeRequest;
            Request = request;
        }

        public string BundleId { get; set; } = Guid.NewGuid().ToString();
        public IUIChangeRequest UIChangeRequest => Request;
        public object NativeRequest { get; }
        public IRequestBase Request { get; }
    }
}
