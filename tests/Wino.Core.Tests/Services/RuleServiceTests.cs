using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Rules;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class RuleServiceTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Mock<IWinoSynchronizerBase> _synchronizer = new();
    private readonly RuleService _service;

    public RuleServiceTests()
    {
        var factory = new Mock<ISynchronizerFactory>();
        factory.Setup(f => f.GetAccountSynchronizerAsync(_accountId)).ReturnsAsync(_synchronizer.Object);
        _synchronizer.SetupGet(s => s.SupportsInboxRules).Returns(true);
        _synchronizer
            .Setup(s => s.UpdateInboxRulesAsync(It.IsAny<IReadOnlyList<InboxRuleChange>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxRuleUpdateResult.Ok);

        _service = new RuleService(factory.Object);
    }

    [Theory]
    [InlineData(MailProviderType.Exchange, true)]
    [InlineData(MailProviderType.Outlook, false)]
    [InlineData(MailProviderType.Gmail, false)]
    [InlineData(MailProviderType.IMAP4, false)]
    public void SupportsRules_IsExchangeOnly(MailProviderType providerType, bool expected)
        => _service.SupportsRules(new MailAccount { ProviderType = providerType }).Should().Be(expected);

    [Fact]
    public async Task SaveRuleAsync_CreatesWhenIdIsEmptyAndUpdatesOtherwise()
    {
        var created = new RemoteInboxRule { Name = "new" };
        var existing = new RemoteInboxRule { Id = "r1", Name = "old" };

        await _service.SaveRuleAsync(_accountId, created);
        await _service.SaveRuleAsync(_accountId, existing);

        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(
            It.Is<IReadOnlyList<InboxRuleChange>>(c => c.Count == 1 && c[0].Kind == InboxRuleChangeKind.Create && c[0].Rule == created),
            false, It.IsAny<CancellationToken>()), Times.Once);
        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(
            It.Is<IReadOnlyList<InboxRuleChange>>(c => c.Count == 1 && c[0].Kind == InboxRuleChangeKind.Update && c[0].Rule == existing),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveRuleAsync_RefusesReadOnlyRulesWithoutTouchingTheServer()
    {
        var result = await _service.SaveRuleAsync(_accountId, new RemoteInboxRule { Id = "r1", IsReadOnly = true });

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(It.IsAny<IReadOnlyList<InboxRuleChange>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveRuleAsync_ThreadsTheOutlookBlobConsentThrough()
    {
        await _service.SaveRuleAsync(_accountId, new RemoteInboxRule { Name = "n" }, removeOutlookRuleBlob: true);

        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(It.IsAny<IReadOnlyList<InboxRuleChange>>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRuleAsync_SendsADeleteChange()
    {
        await _service.DeleteRuleAsync(_accountId, "r9");

        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(
            It.Is<IReadOnlyList<InboxRuleChange>>(c => c.Count == 1 && c[0].Kind == InboxRuleChangeKind.Delete && c[0].RuleId == "r9"),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReorderRulesAsync_WritesOnlyEditableRulesWhosePriorityChanged()
    {
        var first = new RemoteInboxRule { Id = "a", Priority = 2 };
        var readOnly = new RemoteInboxRule { Id = "b", Priority = 1, IsReadOnly = true };
        var unchanged = new RemoteInboxRule { Id = "c", Priority = 3 };

        var result = await _service.ReorderRulesAsync(_accountId, new[] { first, readOnly, unchanged });

        result.Success.Should().BeTrue();
        first.Priority.Should().Be(1);
        readOnly.Priority.Should().Be(1, "read-only rules keep their server priority");
        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(
            It.Is<IReadOnlyList<InboxRuleChange>>(c => c.Count == 1 && c[0].Rule == first && c[0].Kind == InboxRuleChangeKind.Update),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReorderRulesAsync_WithNothingToChange_DoesNotCallTheServer()
    {
        var rules = new[] { new RemoteInboxRule { Id = "a", Priority = 1 }, new RemoteInboxRule { Id = "b", Priority = 2 } };

        var result = await _service.ReorderRulesAsync(_accountId, rules);

        result.Success.Should().BeTrue();
        _synchronizer.Verify(s => s.UpdateInboxRulesAsync(It.IsAny<IReadOnlyList<InboxRuleChange>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRulesAsync_ThrowsNotSupportedWhenTheSynchronizerHasNoRules()
    {
        _synchronizer.SetupGet(s => s.SupportsInboxRules).Returns(false);

        var act = () => _service.GetRulesAsync(_accountId);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ApplyFailures_AreReturnedAsAFailedResultNotThrown()
    {
        _synchronizer
            .Setup(s => s.UpdateInboxRulesAsync(It.IsAny<IReadOnlyList<InboxRuleChange>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("server said no"));

        var result = await _service.DeleteRuleAsync(_accountId, "r1");

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("server said no");
    }
}
