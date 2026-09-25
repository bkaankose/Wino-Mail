using System;
using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Data;

/// <summary>
/// The briefing shows one day at a time. A card belongs on the day its message arrived and on
/// every day one of its dated smart actions covers.
/// </summary>
public class DailyBriefingDayTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset ReceivedAt = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AppearsOn_ShouldShowOnReceivedDayOnly_WhenNoActionIsDated()
    {
        var fact = CreateFact();

        fact.AppearsOn(new DateOnly(2026, 9, 20), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 21), Utc).Should().BeFalse();
    }

    [Fact]
    public void AppearsOn_ShouldUseTheUsersZone_ForTheReceivedDay()
    {
        var fact = CreateFact() with { ReceivedAt = new DateTimeOffset(2026, 9, 20, 23, 30, 0, TimeSpan.Zero) };
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("PlusTwo", TimeSpan.FromHours(2), "PlusTwo", "PlusTwo");

        fact.AppearsOn(new DateOnly(2026, 9, 21), plusTwo).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 20), plusTwo).Should().BeFalse();
    }

    [Fact]
    public void AppearsOn_ShouldCoverEveryDayOfAMultiDayEvent()
    {
        var fact = CreateFact(new CalendarEventAction("Offsite", "Offsite",
            new DateTime(2026, 9, 22, 9, 0, 0), new DateTime(2026, 9, 24, 17, 0, 0), false, "", "", ""));

        fact.AppearsOn(new DateOnly(2026, 9, 21), Utc).Should().BeFalse();
        fact.AppearsOn(new DateOnly(2026, 9, 22), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 23), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 24), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 25), Utc).Should().BeFalse();
    }

    [Fact]
    public void AppearsOn_ShouldTreatAnAllDayEventEndAsExclusive()
    {
        var fact = CreateFact(new CalendarEventAction("Holiday", "Holiday",
            new DateTime(2026, 9, 22), new DateTime(2026, 9, 24), true, "", "", ""));

        fact.AppearsOn(new DateOnly(2026, 9, 23), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 24), Utc).Should().BeFalse();
    }

    [Fact]
    public void AppearsOn_ShouldCoverAStayFromCheckInToCheckOut()
    {
        var fact = CreateFact(new BookingAction("Hotel", BookingKind.Hotel, "Hotel", "ABC", "",
            new DateTime(2026, 9, 23, 15, 0, 0), new DateTime(2026, 9, 25, 11, 0, 0), ""));

        fact.AppearsOn(new DateOnly(2026, 9, 24), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 25), Utc).Should().BeTrue();
    }

    [Fact]
    public void AppearsOn_ShouldShowOnDueAndDeliveryDays()
    {
        var fact = CreateFact(
            new PaymentDueAction("Due", "Acme", "12.00", "EUR", new DateOnly(2026, 9, 23), null),
            new ShipmentAction("Parcel", "DHL", "1", "", new DateOnly(2026, 9, 24)));

        fact.AppearsOn(new DateOnly(2026, 9, 22), Utc).Should().BeFalse();
        fact.AppearsOn(new DateOnly(2026, 9, 23), Utc).Should().BeTrue();
        fact.AppearsOn(new DateOnly(2026, 9, 24), Utc).Should().BeTrue();
    }

    [Fact]
    public void FormatDay_ShouldNameTodayYesterdayThenTheDate()
    {
        var today = new DateOnly(2026, 9, 25);

        DailyBriefingPanelViewModel.FormatDay(today, today).Should().Be(Translator.DailyBriefing_Today);
        DailyBriefingPanelViewModel.FormatDay(today.AddDays(-1), today).Should().Be(Translator.DailyBriefing_Yesterday);
        DailyBriefingPanelViewModel.FormatDay(today.AddDays(-2), today)
            .Should().Be(new DateOnly(2026, 9, 23).ToString("MMMM d", CultureInfo.CurrentCulture));
    }

    private static DailyBriefingFact CreateFact(params MailSmartAction[] actions)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "remote",
            "hash",
            "Subject",
            "Sender",
            "sender@example.com",
            ReceivedAt,
            new List<string>(),
            "normal",
            actions,
            "Headline",
            "Summary",
            DateTime.UtcNow);
}
