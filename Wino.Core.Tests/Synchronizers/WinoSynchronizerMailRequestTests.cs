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
using Wino.Core.Requests.Folder;
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

    private sealed class TestMailSynchronizer
        : WinoSynchronizer<object, object, object>
    {
        public TestMailSynchronizer()
            : base(new MailAccount { Id = Guid.NewGuid(), Name = "Test account" }, WeakReferenceMessenger.Default)
        {
        }

        public override uint BatchModificationSize => 1;
        public override uint InitialMessageDownloadCountPerFolder => 0;
        public int CreateRootFolderInvocationCount { get; private set; }
        public int ExecuteNativeRequestsInvocationCount { get; private set; }

        public override List<IRequestBundle<object>> CreateRootFolder(CreateRootFolderRequest request)
        {
            CreateRootFolderInvocationCount++;
            return [new TestRequestBundle(new object(), request)];
        }

        public override Task ExecuteNativeRequestsAsync(List<IRequestBundle<object>> batchedRequests, CancellationToken cancellationToken = default)
        {
            ExecuteNativeRequestsInvocationCount++;
            return Task.CompletedTask;
        }

        public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(
            object message,
            MailItemFolder assignedFolder,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<NewMailItemPackage>());

        protected override Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(
            MailSynchronizationOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MailSynchronizationResult.Empty);

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
