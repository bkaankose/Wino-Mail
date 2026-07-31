using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;
using Xunit;
using Xunit.Abstractions;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ICloudCalDavLiveTests
{
    private const string ManualSkipMessage = "Manual live iCloud CalDAV test. Fill credentials/constants in this file and remove Skip to run.";

    // Inline credentials/configuration (manual test by design).
    // For iCloud, ServiceUri is typically https://caldav.icloud.com/
    private const string ServiceUri = "https://caldav.icloud.com/";
    private static readonly CustomServerInformation ServerInformation = new()
    {
        IncomingServerUsername = "",
        IncomingServerPassword = "",
        Address = ""
    };

    // Fixed UTC range for deterministic fetch checks.
    private static readonly DateTimeOffset PeriodStartUtc = new(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEndUtc = new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public ICloudCalDavLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task FetchesEventsForFixedUtcRange_AllCalendars()
    {
        var client = new CalDavClient();
        var settings = GetConnectionSettings();

        var calendars = await client.DiscoverCalendarsAsync(settings);
        calendars.Should().NotBeNull();
        calendars.Should().NotBeEmpty();

        foreach (var calendar in calendars)
        {
            var events = await client.GetCalendarEventsAsync(settings, calendar, PeriodStartUtc, PeriodEndUtc);
            _output.WriteLine($"Calendar: {calendar.Name} ({calendar.RemoteCalendarId}) => {events.Count} events");
        }
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task ParsesRequiredIcsFields_ForFetchedEvents()
    {
        var client = new CalDavClient();
        var settings = GetConnectionSettings();

        var calendars = await client.DiscoverCalendarsAsync(settings);
        calendars.Should().NotBeNull();
        calendars.Should().NotBeEmpty();

        foreach (var calendar in calendars)
        {
            var events = await client.GetCalendarEventsAsync(settings, calendar, PeriodStartUtc, PeriodEndUtc);

            foreach (var item in events)
            {
                item.Uid.Should().NotBeNullOrWhiteSpace();
                item.Start.Should().NotBe(default(DateTimeOffset));
                item.Title.Should().NotBeNull();
            }
        }
    }

    [Fact]
    public void BuildsConnectionSettings_FromCustomServerInformation()
    {
        var settings = GetConnectionSettings();

        settings.ServiceUri.Should().Be(new Uri(ServiceUri));
        settings.Username.Should().Be(string.IsNullOrWhiteSpace(ServerInformation.IncomingServerUsername)
            ? ServerInformation.Address
            : ServerInformation.IncomingServerUsername);
        settings.Password.Should().Be(ServerInformation.IncomingServerPassword);
    }

    private static CalDavConnectionSettings GetConnectionSettings()
        => new()
        {
            ServiceUri = new Uri(ServiceUri),
            Username = string.IsNullOrWhiteSpace(ServerInformation.IncomingServerUsername)
                ? ServerInformation.Address
                : ServerInformation.IncomingServerUsername,
            Password = ServerInformation.IncomingServerPassword
        };
}
