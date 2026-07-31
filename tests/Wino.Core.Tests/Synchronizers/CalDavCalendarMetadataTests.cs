using System.Reflection;
using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class CalDavCalendarMetadataTests
{
    [Fact]
    public void ParseCalendarCollection_MapsCollectionMetadataAndSkipsNonEventCalendars()
    {
        var xml = XDocument.Parse(
            """
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:CS="http://calendarserver.org/ns/" xmlns:ICAL="http://apple.com/ns/ical/">
              <D:response>
                <D:href>/calendars/work/</D:href>
                <D:propstat>
                  <D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop>
                    <D:resourcetype>
                      <D:collection />
                      <C:calendar />
                    </D:resourcetype>
                    <D:displayname>Work</D:displayname>
                    <C:calendar-description>Team calendar</C:calendar-description>
                    <CS:getctag>"ctag-1"</CS:getctag>
                    <D:sync-token>sync-1</D:sync-token>
                    <D:current-user-privilege-set>
                      <D:privilege>
                        <D:read />
                      </D:privilege>
                    </D:current-user-privilege-set>
                    <C:calendar-timezone><![CDATA[
                    BEGIN:VCALENDAR
                    BEGIN:VTIMEZONE
                    TZID:Europe/Warsaw
                    END:VTIMEZONE
                    END:VCALENDAR
                    ]]></C:calendar-timezone>
                    <C:supported-calendar-component-set>
                      <C:comp name="VEVENT" />
                      <C:comp name="VTODO" />
                    </C:supported-calendar-component-set>
                    <C:schedule-calendar-transp>
                      <C:transparent />
                    </C:schedule-calendar-transp>
                    <ICAL:calendar-color>#5b617aff</ICAL:calendar-color>
                    <ICAL:calendar-order>2</ICAL:calendar-order>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/calendars/tasks/</D:href>
                <D:propstat>
                  <D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop>
                    <D:resourcetype>
                      <D:collection />
                      <C:calendar />
                    </D:resourcetype>
                    <D:displayname>Tasks</D:displayname>
                    <C:supported-calendar-component-set>
                      <C:comp name="VTODO" />
                    </C:supported-calendar-component-set>
                  </D:prop>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """);

        var calendars = ParseCalendars(xml, new Uri("https://calendar.example.com/"));

        calendars.Should().ContainSingle();

        var calendar = calendars[0];
        calendar.RemoteCalendarId.Should().Be("https://calendar.example.com/calendars/work");
        calendar.Name.Should().Be("Work");
        calendar.Description.Should().Be("Team calendar");
        calendar.CTag.Should().Be("\"ctag-1\"");
        calendar.SyncToken.Should().Be("sync-1");
        calendar.TimeZone.Should().Be("Europe/Warsaw");
        calendar.BackgroundColorHex.Should().Be("#5B617A");
        calendar.IsReadOnly.Should().BeTrue();
        calendar.SupportsEvents.Should().BeTrue();
        calendar.DefaultShowAs.Should().Be(CalendarItemShowAs.Free);
        calendar.Order.Should().Be(2d);
    }

    [Fact]
    public async Task SynchronizeCalendarMetadataAsync_UpdatesServerBackedSettingsAndPreservesUserColorOverride()
    {
        var tempDirectory = CreateTempDirectory();

        var serverInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            IncomingServer = "imap.example.com",
            IncomingServerPort = "993",
            IncomingServerUsername = "user@example.com",
            IncomingServerPassword = "password",
            OutgoingServer = "smtp.example.com",
            OutgoingServerPort = "587",
            OutgoingServerUsername = "user@example.com",
            OutgoingServerPassword = "password",
            MaxConcurrentClients = 5,
            CalendarSupportMode = ImapCalendarSupportMode.CalDav
        };

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Test",
            Address = "test@example.com",
            ProviderType = MailProviderType.IMAP4,
            IsCalendarAccessGranted = true,
            ServerInformation = serverInformation
        };

        var localCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            RemoteCalendarId = "https://calendar.example.com/calendars/work",
            Name = "Local",
            BackgroundColorHex = "#123456",
            TextColorHex = "#FFFFFF",
            IsBackgroundColorUserOverridden = true,
            TimeZone = "UTC",
            IsReadOnly = false,
            DefaultShowAs = CalendarItemShowAs.Busy
        };

        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor
            .Setup(x => x.GetAccountCalendarsAsync(account.Id))
            .ReturnsAsync(new List<AccountCalendar> { localCalendar });
        changeProcessor
            .Setup(x => x.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns(Task.CompletedTask);
        changeProcessor
            .Setup(x => x.DeleteCalendarIcsForCalendarAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        changeProcessor
            .Setup(x => x.DeleteAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns(Task.CompletedTask);
        changeProcessor
            .Setup(x => x.InsertAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(tempDirectory, account, changeProcessor.Object);

        try
        {
            await InvokePrivateAsync(
                synchronizer,
                "SynchronizeCalendarMetadataAsync",
                new List<CalDavCalendar>
                {
                    new()
                    {
                        RemoteCalendarId = localCalendar.RemoteCalendarId,
                        Name = "Remote",
                        TimeZone = "Europe/Warsaw",
                        BackgroundColorHex = "#ABCDEF",
                        IsReadOnly = true,
                        DefaultShowAs = CalendarItemShowAs.Free,
                        Order = 0
                    }
                });

            localCalendar.Name.Should().Be("Remote");
            localCalendar.TimeZone.Should().Be("Europe/Warsaw");
            localCalendar.IsReadOnly.Should().BeTrue();
            localCalendar.DefaultShowAs.Should().Be(CalendarItemShowAs.Free);
            localCalendar.IsPrimary.Should().BeTrue();
            localCalendar.BackgroundColorHex.Should().Be("#123456");
            localCalendar.TextColorHex.Should().Be(ColorHelpers.GetReadableTextColorHex("#123456"));

            changeProcessor.Verify(x => x.UpdateAccountCalendarAsync(localCalendar), Times.Once);
            changeProcessor.Verify(x => x.InsertAccountCalendarAsync(It.IsAny<AccountCalendar>()), Times.Never);
            changeProcessor.Verify(x => x.DeleteAccountCalendarAsync(It.IsAny<AccountCalendar>()), Times.Never);
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    private static List<CalDavCalendar> ParseCalendars(XDocument xml, Uri baseUri)
    {
        var parseMethod = typeof(CalDavClient).GetMethod(
            "ParseCalendarCollection",
            BindingFlags.NonPublic | BindingFlags.Static);

        parseMethod.Should().NotBeNull();

        var result = parseMethod!.Invoke(null, [xml, baseUri]);
        return result.Should().BeOfType<List<CalDavCalendar>>().Subject;
    }

    private static ImapSynchronizer CreateSynchronizer(string appDataFolder, MailAccount account, IImapChangeProcessor changeProcessor)
    {
        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupProperty(x => x.ApplicationDataFolderPath, appDataFolder);
        applicationConfiguration.SetupProperty(x => x.PublisherSharedFolderPath, appDataFolder);
        applicationConfiguration.SetupProperty(x => x.ApplicationTempFolderPath, appDataFolder);
        applicationConfiguration.SetupGet(x => x.SentryDNS).Returns(string.Empty);

        var unifiedSynchronizer = new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        return new ImapSynchronizer(
            account,
            changeProcessor,
            applicationConfiguration.Object,
            unifiedSynchronizer,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            Mock.Of<ICalDavClient>(),
            Mock.Of<IAutoDiscoveryService>(),
            Mock.Of<ICalendarService>());
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wino-caldav-calendar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] parameters)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var task = (Task)method.Invoke(instance, parameters)!;
        await task.ConfigureAwait(false);
    }
}
