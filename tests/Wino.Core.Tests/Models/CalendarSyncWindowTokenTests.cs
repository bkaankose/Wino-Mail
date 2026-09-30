using FluentAssertions;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class CalendarSyncWindowTokenTests
{
    [Fact]
    public void Encode_RoundTripsProviderTokenAndWindowEnd()
    {
        var windowEnd = new DateTimeOffset(2028, 9, 30, 13, 45, 0, TimeSpan.Zero);
        var stored = CalendarSyncWindowToken.Encode("sync-1|ctag-1", windowEnd);

        stored.Should().Be("win=20280930|sync-1|ctag-1");

        var (providerToken, decodedWindowEnd) = CalendarSyncWindowToken.Decode(stored);
        providerToken.Should().Be("sync-1|ctag-1");
        decodedWindowEnd.Should().Be(new DateTimeOffset(2028, 9, 30, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_EmptyToken_ReturnsEmptyProviderTokenWithoutWindow(string? stored)
    {
        var (providerToken, windowEnd) = CalendarSyncWindowToken.Decode(stored!);

        providerToken.Should().BeEmpty();
        windowEnd.Should().BeNull();
    }

    [Theory]
    [InlineData("legacy-delta-token")]
    [InlineData("sync-1|ctag-1")]
    [InlineData("win=notadate|token")]
    [InlineData("win=20280930")]
    public void Decode_LegacyToken_KeepsTokenAndReportsNoWindow(string stored)
    {
        var (providerToken, windowEnd) = CalendarSyncWindowToken.Decode(stored);

        providerToken.Should().Be(stored);
        windowEnd.Should().BeNull();
    }

    [Fact]
    public void RequiresReanchor_LegacyTokenWithoutWindow_IsTrue()
    {
        CalendarSyncWindowToken.RequiresReanchor(null, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void RequiresReanchor_WindowEndFarAway_IsFalse()
    {
        var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

        CalendarSyncWindowToken.RequiresReanchor(now.AddYears(2), now).Should().BeFalse();
        CalendarSyncWindowToken.RequiresReanchor(now.Add(CalendarSyncWindowToken.ReanchorThreshold).AddDays(1), now).Should().BeFalse();
    }

    [Fact]
    public void RequiresReanchor_WindowEndWithinThreshold_IsTrue()
    {
        var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

        CalendarSyncWindowToken.RequiresReanchor(now.AddDays(100), now).Should().BeTrue();
        CalendarSyncWindowToken.RequiresReanchor(now.AddDays(-1), now).Should().BeTrue();
    }
}
