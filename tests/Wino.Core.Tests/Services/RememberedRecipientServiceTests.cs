using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// The remembered-recipient list as the composer sees it.
///
/// Everything here is about the two things that cost a user something. The list is whole rather than a
/// query, so it must be read once and filtered in memory - a read per keystroke would be unusable. And
/// an account that has no list must be remembered as having none, or every keystroke on it resolves a
/// synchronizer again, which is a database read and a credential unprotect for an answer that cannot
/// change.
/// </summary>
public class RememberedRecipientServiceTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private sealed class Harness
    {
        public readonly Mock<IMailSynchronizerFactory> Factory = new();
        public readonly Mock<IMailSynchronizer> Synchronizer = new();
        public int Reads;
        public int FactoryCalls;
        public List<RememberedRecipient> Recorded = [];

        public Harness(bool remembers = true, IReadOnlyList<RememberedRecipient> list = null, Exception readThrows = null)
        {
            Synchronizer.SetupGet(s => s.Capabilities).Returns(new MailSynchronizerCapabilities(true, true, remembers));

            Synchronizer
                .Setup(s => s.GetRememberedRecipientsAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref Reads);

                    if (readThrows is not null)
                        return Task.FromException<IReadOnlyList<RememberedRecipient>>(readThrows);

                    return Task.FromResult(list ?? []);
                });

            Synchronizer
                .Setup(s => s.RememberRecipientsAsync(It.IsAny<IReadOnlyList<RememberedRecipient>>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<RememberedRecipient> r, CancellationToken _) =>
                {
                    Recorded.AddRange(r);
                    return Task.CompletedTask;
                });

            Factory
                .Setup(f => f.GetSynchronizerAsync(It.IsAny<Guid>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref FactoryCalls);
                    return Task.FromResult(Synchronizer.Object);
                });
        }

        public RememberedRecipientService Build() => new(Factory.Object);
    }

    private static RememberedRecipient Person(string address, string name, int weight)
        => new(address, name, weight);

    [Fact]
    public async Task SuggestsOnAddressAndOnDisplayName()
    {
        var harness = new Harness(list: [Person("rwilson@example.test", "Rebecca Wilson", 100)]);
        var service = harness.Build();

        (await service.SuggestAsync(Account, "rwil")).Should().HaveCount(1);
        (await service.SuggestAsync(Account, "rebec")).Should().HaveCount(1);
        (await service.SuggestAsync(Account, "nobody")).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepsTheOrderTheProviderGaveThem()
    {
        // The provider ranks them; re-sorting here would be second-guessing the weights it just set.
        var harness = new Harness(list:
        [
            Person("often@example.test", "Often", 900),
            Person("rarely@example.test", "Rarely", 10),
        ]);

        var suggestions = await harness.Build().SuggestAsync(Account, "example");

        suggestions.Select(s => s.Address).Should().Equal("often@example.test", "rarely@example.test");
    }

    [Fact]
    public async Task ReadsTheListOnceHoweverManyKeystrokesFollow()
    {
        var harness = new Harness(list: [Person("a@example.test", "A", 1)]);
        var service = harness.Build();

        for (var i = 0; i < 6; i++)
            await service.SuggestAsync(Account, "exam");

        harness.Reads.Should().Be(1);
    }

    [Fact]
    public async Task AnAccountWithNoListIsAskedOnce()
    {
        // The expensive half is the factory: resolving a synchronizer reads the account and unprotects
        // its secrets, so an unremembered miss costs that on every keystroke.
        var harness = new Harness(remembers: false);
        var service = harness.Build();

        for (var i = 0; i < 6; i++)
            (await service.SuggestAsync(Account, "exam")).Should().BeEmpty();

        harness.FactoryCalls.Should().Be(1);
        harness.Reads.Should().Be(0, "a provider without a list should never be asked for one");
    }

    [Fact]
    public async Task AFailedReadIsNotRetriedPerKeystroke()
    {
        var harness = new Harness(readThrows: new InvalidOperationException("mailbox said no"));
        var service = harness.Build();

        for (var i = 0; i < 4; i++)
            (await service.SuggestAsync(Account, "exam")).Should().BeEmpty();

        harness.Reads.Should().Be(1, "a mailbox that is already failing should not be hammered");
    }

    [Fact]
    public async Task RecordingHandsTheAddressesToTheProvider()
    {
        var harness = new Harness(list: [Person("known@example.test", "Known", 50)]);

        await harness.Build().RecordAsync(Account, [Person("new@example.test", "New", 0)]);

        harness.Recorded.Select(r => r.Address).Should().Equal("new@example.test");
        harness.Reads.Should().Be(0, "nothing has asked for this account's list, so there is none to refresh");
    }

    [Fact]
    public async Task RecordingRefreshesAListThatIsAlreadyInMemory()
    {
        // The provider owns the weights - it decides what a send is worth - so once a list is held,
        // the held copy is replaced from the provider rather than patched with a guess here.
        var harness = new Harness(list: [Person("known@example.test", "Known", 50)]);
        var service = harness.Build();

        await service.SuggestAsync(Account, "known");
        harness.Reads.Should().Be(1);

        await service.RecordAsync(Account, [Person("new@example.test", "New", 0)]);

        harness.Reads.Should().Be(2, "the held list is re-read after a write so the new weights are used");
    }

    [Fact]
    public async Task RecordingOnAnAccountWithNoListDoesNothing()
    {
        var harness = new Harness(remembers: false);

        await harness.Build().RecordAsync(Account, [Person("new@example.test", "New", 0)]);

        harness.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task AProviderThatThrowsWhileRecordingDoesNotFailTheSend()
    {
        var harness = new Harness(list: []);
        harness.Synchronizer
            .Setup(s => s.RememberRecipientsAsync(It.IsAny<IReadOnlyList<RememberedRecipient>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mailbox said no"));

        var record = async () => await harness.Build().RecordAsync(Account, [Person("new@example.test", "New", 0)]);

        await record.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        // Every other failure degrades to "no suggestions"; cancellation has to propagate, or a
        // superseded keystroke looks like an empty result instead of an abandoned one.
        var harness = new Harness(readThrows: new OperationCanceledException());

        var suggest = async () => await harness.Build().SuggestAsync(Account, "exam", 10, new CancellationToken(canceled: true));

        await suggest.Should().ThrowAsync<OperationCanceledException>();
    }
}
