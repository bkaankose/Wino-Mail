using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class WinoSynchronizerCalendarRequestTests
{
    [Fact]
    public async Task Calendar_request_failure_should_complete_actions_and_reset_state()
    {
        var recipient = new SynchronizationActionsCompletedRecipient();
        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            var synchronizer = new TestCalendarSynchronizer(throwDuringRequestExecution: true);
            var calendarItemId = Guid.NewGuid();

            synchronizer.QueueRequest(new DeleteCalendarEventRequest(new CalendarItem { Id = calendarItemId }));

            var result = await synchronizer.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions
            {
                AccountId = synchronizer.Account.Id,
                Type = CalendarSynchronizationType.ExecuteRequests
            });

            result.CompletedState.Should().Be(SynchronizationCompletedState.Failed);
            synchronizer.State.Should().Be(AccountSynchronizerState.Idle);
            synchronizer.GetPendingCalendarOperationIds().Should().BeEmpty();
            recipient.CompletedAccountIds.Should().ContainSingle().Which.Should().Be(synchronizer.Account.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Calendar_request_success_should_complete_actions_and_reset_state()
    {
        var recipient = new SynchronizationActionsCompletedRecipient();
        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            var synchronizer = new TestCalendarSynchronizer(throwDuringRequestExecution: false);
            var calendarItemId = Guid.NewGuid();

            synchronizer.QueueRequest(new DeleteCalendarEventRequest(new CalendarItem { Id = calendarItemId }));

            var result = await synchronizer.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions
            {
                AccountId = synchronizer.Account.Id,
                Type = CalendarSynchronizationType.ExecuteRequests
            });

            result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
            synchronizer.State.Should().Be(AccountSynchronizerState.Idle);
            synchronizer.GetPendingCalendarOperationIds().Should().BeEmpty();
            recipient.CompletedAccountIds.Should().ContainSingle().Which.Should().Be(synchronizer.Account.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Change_start_and_end_date_request_should_dispatch_to_matching_handler()
    {
        var synchronizer = new TestCalendarSynchronizer(throwDuringRequestExecution: false);
        var calendarItemId = Guid.NewGuid();
        var request = new ChangeStartAndEndDateRequest(
            new CalendarItem { Id = calendarItemId },
            []);

        synchronizer.QueueRequest(request);

        var result = await synchronizer.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions
        {
            AccountId = synchronizer.Account.Id,
            Type = CalendarSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        synchronizer.ChangeStartAndEndDateInvocationCount.Should().Be(1);
    }

    public sealed class SynchronizationActionsCompletedRecipient : IRecipient<SynchronizationActionsCompleted>
    {
        public List<Guid> CompletedAccountIds { get; } = [];

        public void Receive(SynchronizationActionsCompleted message) => CompletedAccountIds.Add(message.AccountId);
    }

    private sealed class TestCalendarSynchronizer : WinoSynchronizer<object, object, object>
    {
        private readonly bool _throwDuringRequestExecution;

        public TestCalendarSynchronizer(bool throwDuringRequestExecution)
            : base(new MailAccount { Id = Guid.NewGuid(), Name = "Test account" }, WeakReferenceMessenger.Default)
        {
            _throwDuringRequestExecution = throwDuringRequestExecution;
        }

        public override uint BatchModificationSize => 1;
        public override uint InitialMessageDownloadCountPerFolder => 0;
        public int ChangeStartAndEndDateInvocationCount { get; private set; }

        public override Task ExecuteNativeRequestsAsync(List<IRequestBundle<object>> batchedRequests, CancellationToken cancellationToken = default)
            => _throwDuringRequestExecution
                ? Task.FromException(new InvalidOperationException("Calendar request execution failed."))
                : Task.CompletedTask;

        public override List<IRequestBundle<object>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
            => [new TestRequestBundle(new object(), request)];

        public override List<IRequestBundle<object>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        {
            ChangeStartAndEndDateInvocationCount++;
            return [new TestRequestBundle(new object(), request)];
        }

        public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(
            object message,
            Wino.Core.Domain.Entities.Mail.MailItemFolder assignedFolder,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<NewMailItemPackage>());

        protected override Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(MailSynchronizationResult.Empty);

        protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
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
