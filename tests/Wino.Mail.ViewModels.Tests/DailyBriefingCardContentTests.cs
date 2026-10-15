using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class DailyBriefingCardContentTests
{
    private static readonly DailyBriefingAccount Account = new(new MailAccount { Id = Guid.NewGuid(), Name = "Sample", Address = "sample@example.com" });

    private static DailyBriefingItem Item(string headline, string priority, params string[] labels)
        => new(new DailyBriefingFact(Account.Account.Id, Guid.NewGuid(), "remote", "hash", "The subject", "Sender", "sender@example.com",
            new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero), labels, priority, new List<MailSmartAction>(), headline, "One line.", DateTime.UtcNow), Account);

    [Fact]
    public void Headline_FallsBackToTheSubject_BeforeEnrichment()
    {
        var card = DailyBriefingCardContent.From(Item(string.Empty, "normal"), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        card.Headline.Should().Be("The subject");
        card.Time.Should().Be("09:30");
        card.Summary.Should().Be("One line.");
        card.Urgency.Should().BeNull();
    }

    [Fact]
    public void PriorityChip_TakesOneOfTheTwoLabelPlaces()
    {
        var urgent = DailyBriefingCardContent.From(Item("Headline", "urgent", "action", "social", "finance"), TimeZoneInfo.Utc);
        var normal = DailyBriefingCardContent.From(Item("Headline", "normal", "action", "social", "finance"), TimeZoneInfo.Utc);

        urgent.Urgency.Should().Be(Translator.DailyBriefing_UrgencyUrgent);
        urgent.Labels.Should().HaveCount(1);
        normal.Labels.Should().HaveCount(DailyBriefingCardContent.MaxChipsPerRow);
    }

    [Fact]
    public async Task Availability_IsOffWithoutTheEntitlement_AndNeverReadsTheStore()
    {
        var entitlements = new Mock<IWinoAccountIntelligenceSnapshotService>();
        entitlements.Setup(s => s.GetEntitlementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow));
        var local = new Mock<ILocalIntelligenceService>(MockBehavior.Strict);

        var state = await DailyBriefingAvailability.ReadAsync(entitlements.Object, local.Object);

        state.Should().Be(DailyBriefingShellState.Unavailable);
    }

    [Fact]
    public async Task Availability_IsUnseenOnlyWithAnEligibleAccountAndUnseenContent()
    {
        var entitlements = new Mock<IWinoAccountIntelligenceSnapshotService>();
        entitlements.Setup(s => s.GetEntitlementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoIntelligenceEntitlementSnapshot(WinoIntelligenceEntitlementState.Active, Guid.NewGuid(), DateTimeOffset.UtcNow));
        var local = new Mock<ILocalIntelligenceService>();
        local.Setup(s => s.GetEligibleAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DailyBriefingAccount>());

        (await DailyBriefingAvailability.ReadAsync(entitlements.Object, local.Object)).Should().Be(new DailyBriefingShellState(true, false));

        local.Setup(s => s.GetEligibleAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DailyBriefingAccount> { Account });
        local.Setup(s => s.GetUnseenStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DailyBriefingUnseenState(true, null));

        (await DailyBriefingAvailability.ReadAsync(entitlements.Object, local.Object)).Should().Be(new DailyBriefingShellState(true, true));
    }
}
