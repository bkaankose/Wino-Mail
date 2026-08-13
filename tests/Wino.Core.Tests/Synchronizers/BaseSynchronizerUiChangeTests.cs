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
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class BaseSynchronizerUiChangeTests
{
    [Fact]
    public void ApplyOptimisticUiChanges_UsesBundleUiChangeRequest_ForBatchBundle()
    {
        WeakReferenceMessenger.Default.Reset();

        var folderId = Guid.NewGuid();
        var account = new MailAccount { Id = Guid.NewGuid(), Name = "Test account" };
        var synchronizer = new TestSynchronizer(account);
        var recipient = new UiChangeRecipient();

        var request1 = new MarkReadRequest(CreateMailCopy(folderId, isRead: false), IsRead: true);
        var request2 = new MarkReadRequest(CreateMailCopy(folderId, isRead: false), IsRead: true);
        var batchRequest = new BatchMarkReadRequest([request1, request2]);
        var bundle = new HttpRequestBundle<object>(new object(), batchRequest, request1);

        WeakReferenceMessenger.Default.Register<MailStateUpdatedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailStateUpdatedMessage>(recipient);

        try
        {
            synchronizer.ApplyUiChanges([bundle]);

            recipient.SingleUpdates.Should().BeEmpty();
            recipient.BulkUpdates.Should().ContainSingle();
            recipient.BulkUpdates[0].UpdatedStates.Should().HaveCount(2);
            request1.Item.IsRead.Should().BeFalse();
            request2.Item.IsRead.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<MailStateUpdatedMessage>(recipient);
            WeakReferenceMessenger.Default.Unregister<BulkMailStateUpdatedMessage>(recipient);
            WeakReferenceMessenger.Default.Reset();
        }
    }

    private static MailCopy CreateMailCopy(Guid folderId, bool isRead) =>
        new()
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = folderId,
            IsRead = isRead
        };

    private sealed class TestSynchronizer : BaseSynchronizer<object>
    {
        public TestSynchronizer(MailAccount account)
            : base(account, WeakReferenceMessenger.Default)
        {
        }

        public void ApplyUiChanges(List<IRequestBundle<object>> bundles) => ApplyOptimisticUiChanges(bundles);

        public override Task ExecuteNativeRequestsAsync(List<IRequestBundle<object>> batchedRequests, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    internal sealed class UiChangeRecipient : IRecipient<MailStateUpdatedMessage>, IRecipient<BulkMailStateUpdatedMessage>
    {
        public List<MailStateUpdatedMessage> SingleUpdates { get; } = [];
        public List<BulkMailStateUpdatedMessage> BulkUpdates { get; } = [];

        public void Receive(MailStateUpdatedMessage message) => SingleUpdates.Add(message);
        public void Receive(BulkMailStateUpdatedMessage message) => BulkUpdates.Add(message);
    }
}
