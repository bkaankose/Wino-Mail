using System.Net;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Synchronizers.CalDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Quota, max-resource-size and max-attendees-per-instance: checked before a PUT, mapped from the
/// server's refusal, and the known quota lowered after a write.
/// </summary>
public sealed class CalDavLimitTests : IAsyncLifetime
{
    private CalDavEngineTestHost _host = null!;

    public async Task InitializeAsync() => _host = await CalDavEngineTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    #region Pre-checks

    [Fact]
    public void ResourceLargerThanMaxResourceSize_IsRefused()
    {
        var ics = EventWithAttendees(0);
        var state = new CalDavCalendarState { MaxResourceSize = Encoding.UTF8.GetByteCount(ics) - 1 };

        var exception = Assert.Throws<DavRequestException>(() => CalDavSynchronizationEngine.EnsureWithinServerLimits(state, ics, null));

        exception.Message.Should().Be(Translator.DavError_ResourceTooLarge);
        exception.StatusCode.Should().Be(403);
        exception.HasError("max-resource-size").Should().BeTrue();
    }

    [Fact]
    public void ZeroOrMissingLimits_MeanUnlimited()
    {
        var ics = EventWithAttendees(500);

        CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState { MaxResourceSize = 0, MaxAttendeesPerInstance = 0 }, ics, null);
        CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState(), ics, null);
    }

    [Fact]
    public void MoreAttendeesThanAllowed_IsRefused_ButAnExistingOversizedEventMayStillChange()
    {
        var state = new CalDavCalendarState { MaxAttendeesPerInstance = 2 };

        var exception = Assert.Throws<DavRequestException>(() => CalDavSynchronizationEngine.EnsureWithinServerLimits(state, EventWithAttendees(3), EventWithAttendees(2)));
        exception.Message.Should().Be(Translator.DavError_TooManyAttendees);
        exception.HasError("max-attendees-per-instance").Should().BeTrue();

        CalDavSynchronizationEngine.EnsureWithinServerLimits(state, EventWithAttendees(3), EventWithAttendees(3));
        CalDavSynchronizationEngine.EnsureWithinServerLimits(state, EventWithAttendees(2), null);
    }

    [Fact]
    public void AttendeeCount_IsPerInstance_AndIgnoresAlarmRecipients_AndFoldedLines()
    {
        const string ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nATTENDEE;CN=A:mailto:a@x\r\nATTENDEE;CN=\"Very long\r\n name\":mailto:b@x\r\n" +
            "BEGIN:VALARM\r\nACTION:EMAIL\r\nATTENDEE:mailto:me@x\r\nATTENDEE:mailto:me2@x\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID:20260101T090000Z\r\nATTENDEE:mailto:a@x\r\nATTENDEE:mailto:b@x\r\nATTENDEE:mailto:c@x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        CalDavSynchronizationEngine.CountMaximumAttendeesPerInstance(ics).Should().Be(3);
        CalDavSynchronizationEngine.CountMaximumAttendeesPerInstance(null).Should().Be(0);
    }

    [Fact]
    public void Quota_RefusesAGrowthLargerThanTheAvailableBytes_AndCountsOnlyTheGrowthOfAnUpdate()
    {
        var previous = EventWithAttendees(10);
        var larger = EventWithAttendees(12);
        var growth = Encoding.UTF8.GetByteCount(larger) - Encoding.UTF8.GetByteCount(previous);

        var exception = Assert.Throws<DavRequestException>(() =>
            CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState { QuotaAvailableBytes = growth + 10 }, larger, null));
        exception.Message.Should().Be(Translator.DavError_QuotaExceeded);
        exception.StatusCode.Should().Be(507);

        CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState { QuotaAvailableBytes = growth }, larger, previous);
        Assert.Throws<DavRequestException>(() =>
            CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState { QuotaAvailableBytes = growth - 1 }, larger, previous));
        CalDavSynchronizationEngine.EnsureWithinServerLimits(new CalDavCalendarState { QuotaAvailableBytes = 0 }, previous, larger);
    }

    [Fact]
    public void ApplyQuotaDelta_MovesAvailableAndUsedBytes_WithoutGoingNegative()
    {
        var state = new CalDavCalendarState { QuotaAvailableBytes = 1000, QuotaUsedBytes = 50 };

        CalDavSynchronizationEngine.ApplyQuotaDelta(state, 300);
        state.QuotaAvailableBytes.Should().Be(700);
        state.QuotaUsedBytes.Should().Be(350);

        CalDavSynchronizationEngine.ApplyQuotaDelta(state, -400);
        state.QuotaAvailableBytes.Should().Be(1100);
        state.QuotaUsedBytes.Should().Be(0);

        var unknown = new CalDavCalendarState();
        CalDavSynchronizationEngine.ApplyQuotaDelta(unknown, 300);
        unknown.QuotaAvailableBytes.Should().BeNull();
    }

    #endregion

    #region Engine

    [Fact]
    public async Task Create_OverQuota_IsRefusedBeforeAnyPut()
    {
        _host.Transport.RewriteHomeListing = body => body.Replace("<D:quota-available-bytes>1048576</D:quota-available-bytes>", "<D:quota-available-bytes>100</D:quota-available-bytes>");
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("Too big for the quota");

        var create = () => _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: []);

        (await create.Should().ThrowAsync<DavRequestException>()).Which.Message.Should().Be(Translator.DavError_QuotaExceeded);
        _host.Transport.PutBodies.Should().BeEmpty();
        (await _host.CalendarService.GetCalendarItemAsync(item.Id)).Should().BeNull("a refused create leaves nothing behind");
    }

    [Fact]
    public async Task Create_WithTooManyAttendees_IsRefusedBeforeAnyPut()
    {
        _host.Transport.RewriteHomeListing = body => body.Replace("<C:max-attendees-per-instance>300</C:max-attendees-per-instance>", "<C:max-attendees-per-instance>1</C:max-attendees-per-instance>");
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("Meeting");
        var attendees = new List<CalendarEventAttendee>
        {
            new() { Id = Guid.NewGuid(), CalendarItemId = item.Id, Email = "a@example.test", Name = "A" },
            new() { Id = Guid.NewGuid(), CalendarItemId = item.Id, Email = "b@example.test", Name = "B" }
        };

        var create = () => _host.Engine.CreateEventAsync(_host.Account, item, attendees, reminders: []);

        (await create.Should().ThrowAsync<DavRequestException>()).Which.Message.Should().Be(Translator.DavError_TooManyAttendees);
        _host.Transport.PutBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulWrites_AdjustTheKnownQuota()
    {
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("Quota");

        await _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: []);

        var created = Encoding.UTF8.GetByteCount(_host.LastPutBody());
        var state = (await _host.StateStore.GetAsync(_host.Account.Id)).FindCalendar(FakeCalDavServer.CalendarId)!;
        state.QuotaAvailableBytes.Should().Be(1048576 - created);
        state.QuotaUsedBytes.Should().Be(2048 + created);

        var stored = (await _host.CalendarService.GetCalendarItemAsync(item.Id))!;
        stored.Title = "Quota, now with a much longer title than before";
        await _host.Engine.UpdateEventAsync(_host.Account, stored, attendees: null);

        var updated = Encoding.UTF8.GetByteCount(_host.LastPutBody());
        state = (await _host.StateStore.GetAsync(_host.Account.Id)).FindCalendar(FakeCalDavServer.CalendarId)!;
        state.QuotaAvailableBytes.Should().Be(1048576 - updated, "an update only counts its growth over the stored copy");
    }

    [Fact]
    public async Task ServerRefusal_507_SurfacesAsQuotaExceeded()
    {
        await _host.SyncAsync();
        _host.Transport.AnswerPut = _ => new HttpResponseMessage(HttpStatusCode.InsufficientStorage);
        var item = await _host.NewItemAsync("Server says full");

        var create = () => _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: []);

        var exception = (await create.Should().ThrowAsync<DavRequestException>()).Which;
        exception.StatusCode.Should().Be(507);
        exception.Message.Should().Be(Translator.DavError_QuotaExceeded);
    }

    #endregion

    #region Server error mapping

    [Theory]
    [InlineData(507, "", "quota")]
    [InlineData(403, "<D:quota-not-exceeded />", "quota")]
    [InlineData(507, "<D:sufficient-disk-space />", "quota")]
    [InlineData(403, "<C:max-resource-size />", "size")]
    [InlineData(413, "<C:max-resource-size />", "size")]
    [InlineData(403, "<C:max-attendees-per-instance />", "attendees")]
    [InlineData(403, "<D:need-privileges />", "permission")]
    [InlineData(503, "", "unavailable")]
    public async Task ResponseHandler_MapsStoragePreconditions(int status, string condition, string expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Put, "https://caldav.example.test/a.ics"),
            Content = condition.Length == 0
                ? new StringContent(string.Empty)
                : new StringContent($"<D:error xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">{condition}</D:error>", Encoding.UTF8, "application/xml")
        };

        var exception = await Assert.ThrowsAsync<DavRequestException>(() => new DavResponseHandler().EnsureSuccessAsync(response));

        exception.StatusCode.Should().Be(status);
        exception.Message.Should().Be(expected switch
        {
            "quota" => Translator.DavError_QuotaExceeded,
            "size" => Translator.DavError_ResourceTooLarge,
            "attendees" => Translator.DavError_TooManyAttendees,
            "permission" => Translator.DavError_PermissionDenied,
            _ => Translator.DavError_ServerUnavailable
        });
    }

    #endregion

    private static string EventWithAttendees(int count)
    {
        var builder = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:limit\r\nSUMMARY:Limits\r\n");

        for (var i = 0; i < count; i++)
            builder.Append($"ATTENDEE;CN=Person {i};PARTSTAT=NEEDS-ACTION:mailto:person{i}@example.test\r\n");

        return builder.Append("END:VEVENT\r\nEND:VCALENDAR\r\n").ToString();
    }
}
