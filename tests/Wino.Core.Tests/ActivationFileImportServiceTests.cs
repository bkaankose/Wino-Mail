using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;
using Moq;

namespace Wino.Core.Tests;

public sealed class ActivationFileImportServiceTests : IDisposable
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wino-file-import-{Guid.NewGuid():N}");
    private readonly ActivationFileImportService _service = new(Mock.Of<IWinoLogger>());

    public ActivationFileImportServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task CalendarImport_MapsContentAttendeesReminderAndRepresentableRecurrence()
    {
        var path = WriteFile("event.ics", """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Tests//EN
            BEGIN:VEVENT
            UID:first-event
            DTSTAMP:20260820T120000Z
            DTSTART:20260825T070000Z
            DTEND:20260825T080000Z
            SUMMARY:Planning\, review
            LOCATION:Room\, 2
            DESCRIPTION:Line one\nLine <two>
            ORGANIZER;CN=Owner:mailto:Alias@Example.com
            ATTENDEE;CN=Alex:mailto:Alex@Example.com
            ATTENDEE;CN=Owner:mailto:alias@example.com
            RRULE:FREQ=DAILY;INTERVAL=2;BYDAY=MO,WE,FR
            BEGIN:VALARM
            ACTION:DISPLAY
            DESCRIPTION:Reminder
            TRIGGER:-PT15M
            END:VALARM
            END:VEVENT
            BEGIN:VEVENT
            UID:second-event
            DTSTART:20260826T070000Z
            SUMMARY:Ignored
            END:VEVENT
            END:VCALENDAR
            """);

        var result = await _service.ImportCalendarEventAsync([path]);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Planning, review");
        result.Location.Should().Be("Room, 2");
        result.NotesHtml.Should().Be("Line one<br>Line &lt;two&gt;");
        result.StartDate.Should().Be(new DateTime(2026, 8, 25, 7, 0, 0, DateTimeKind.Utc).ToLocalTime());
        result.EndDate.Should().Be(new DateTime(2026, 8, 25, 8, 0, 0, DateTimeKind.Utc).ToLocalTime());
        result.Attendees.Select(attendee => (attendee.Name, attendee.Email)).Should().ContainInOrder(
            ("Alex", "alex@example.com"),
            ("Owner", "alias@example.com"));
        result.AccountAddressHints.Should().BeEquivalentTo("alex@example.com", "alias@example.com");
        result.ReminderMinutesBeforeStart.Should().Be(15);
        result.Recurrence.Should().NotBeNull();
        result.Recurrence!.Frequency.Should().Be(CalendarItemRecurrenceFrequency.Daily);
        result.Recurrence.Interval.Should().Be(2);
        result.Recurrence.Weekdays.Should().BeEquivalentTo(
            [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        result.RequireCalendarPickerWhenUnresolved.Should().BeTrue();
        result.HasUnsupportedImportContent.Should().BeFalse();
    }

    [Fact]
    public async Task CalendarImport_MapsFloatingAndAllDayDates()
    {
        var floatingPath = WriteFile("floating.ics", CalendarWithEvent("""
            UID:floating
            DTSTART:20260825T090000
            DTEND:20260825T100000
            SUMMARY:Floating
            """));
        var allDayPath = WriteFile("all-day.ics", CalendarWithEvent("""
            UID:all-day
            DTSTART;VALUE=DATE:20260825
            DTEND;VALUE=DATE:20260827
            SUMMARY:All day
            """));

        var floating = await _service.ImportCalendarEventAsync([floatingPath]);
        var allDay = await _service.ImportCalendarEventAsync([allDayPath]);

        floating!.StartDate.Should().Be(new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Unspecified));
        floating.IsAllDay.Should().BeFalse();
        allDay!.StartDate.Should().Be(new DateTime(2026, 8, 25));
        allDay.EndDate.Should().Be(new DateTime(2026, 8, 27));
        allDay.IsAllDay.Should().BeTrue();
    }

    [Fact]
    public async Task CalendarImport_MapsTzidDateUsingItsInstant()
    {
        var path = WriteFile("tzid.ics", CalendarWithEvent("""
            UID:tzid
            DTSTART;TZID=Europe/Warsaw:20260825T090000
            DTEND;TZID=Europe/Warsaw:20260825T100000
            SUMMARY:Zoned
            """));

        var result = await _service.ImportCalendarEventAsync([path]);

        result.Should().NotBeNull();
        result!.StartDate.ToUniversalTime().Should().Be(new DateTime(2026, 8, 25, 7, 0, 0, DateTimeKind.Utc));
        result.EndDate.ToUniversalTime().Should().Be(new DateTime(2026, 8, 25, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CalendarImport_OmitsUnsupportedRecurrenceAndMarksWarning()
    {
        var path = WriteFile("unsupported.ics", CalendarWithEvent("""
            UID:unsupported
            DTSTART:20260825T090000Z
            DTEND:20260825T100000Z
            SUMMARY:Still imported
            URL:https://example.com/event
            RRULE:FREQ=MONTHLY;BYDAY=1MO
            BEGIN:VALARM
            ACTION:DISPLAY
            TRIGGER;RELATED=END:-PT5M
            END:VALARM
            """));

        var result = await _service.ImportCalendarEventAsync([path]);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Still imported");
        result.Recurrence.Should().BeNull();
        result.ReminderMinutesBeforeStart.Should().BeNull();
        result.HasUnsupportedImportContent.Should().BeTrue();
    }

    [Fact]
    public async Task CalendarImport_UsesFirstValidFileAndReturnsNullWhenNoneParse()
    {
        var malformed = WriteFile("malformed.ics", "not a calendar");
        var valid = WriteFile("valid.ics", CalendarWithEvent("""
            UID:valid
            DTSTART:20260825T090000Z
            SUMMARY:Valid event
            """));

        (await _service.ImportCalendarEventAsync([malformed, valid]))!.Title.Should().Be("Valid event");
        (await _service.ImportCalendarEventAsync([malformed])).Should().BeNull();
    }

    [Fact]
    public async Task ContactImport_MapsVCard4EditorFieldsAndDeduplicatesValues()
    {
        var path = WriteFile("contact.vcf", $$"""
            BEGIN:VCARD
            VERSION:4.0
            FN:Jane Doe
            N:Doe;Jane\;Ann;Q.;Dr.;Jr.
            NICKNAME:Janie,JD
            SORT-STRING:Doe\, Jane
            ORG:Acme;Research
            TITLE:Director
            ROLE:Engineer
            X-OFFICE-LOCATION:Building 4
            EMAIL;TYPE=work;PREF=1:jane@example.com
            EMAIL;TYPE=home:JANE@example.com
            TEL;TYPE=cell;PREF=1:+1 555 0100
            TEL;TYPE=home:+1 555 0100
            ADR;TYPE=work:PO123;Suite 4;1 Main\; Annex;Warsaw;Mazovia;00-001;Poland
            IMPP;PREF=1:skype:jane.doe
            IMPP:skype:jane.doe
            URL:https://example.com/jane
            BDAY:1990-05-12
            NOTE:First line\nSecond line
            RELATED;TYPE=manager:Alex Manager
            RELATED;TYPE=child:Jamie Doe
            PHOTO:data:image/png;base64,{{OnePixelPng}}
            END:VCARD
            """);

        var result = await _service.ImportContactAsync([path]);

        result.Should().NotBeNull();
        var contact = result!.Contact;
        contact.DisplayName.Should().Be("Jane Doe");
        contact.GivenName.Should().Be("Jane;Ann");
        contact.Surname.Should().Be("Doe");
        contact.MiddleName.Should().Be("Q.");
        contact.HonorificPrefix.Should().Be("Dr.");
        contact.HonorificSuffix.Should().Be("Jr.");
        contact.Nickname.Should().Be("Janie");
        contact.FileAs.Should().Be("Doe, Jane");
        contact.CompanyName.Should().Be("Acme");
        contact.Department.Should().Be("Research");
        contact.JobTitle.Should().Be("Director");
        contact.Profession.Should().Be("Engineer");
        contact.OfficeLocation.Should().Be("Building 4");
        contact.Website.Should().Be("https://example.com/jane");
        contact.Notes.Should().Be("First line\nSecond line");
        (contact.BirthdayYear, contact.BirthdayMonth, contact.BirthdayDay).Should().Be((1990, 5, 12));
        contact.EmailAddresses.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
        contact.PhoneNumbers.Should().ContainSingle().Which.Kind.Should().Be(ContactPhoneKind.Mobile);
        contact.PostalAddresses.Should().ContainSingle().Which.Street.Should().Be("Suite 4 1 Main; Annex");
        contact.ImAddresses.Should().ContainSingle().Which.Address.Should().Be("jane.doe");
        contact.Relations.Should().HaveCount(2);
        result.PhotoBytes.Should().Equal(Convert.FromBase64String(OnePixelPng));
        result.HasUnsupportedContent.Should().BeFalse();
    }

    [Fact]
    public async Task ContactImport_DecodesVCard21QuotedPrintableAndFoldedBase64()
    {
        var foldedPhoto = OnePixelPng.Insert(48, "\r\n ");
        var path = WriteFile("legacy.vcf", $$"""
            BEGIN:VCARD
            VERSION:2.1
            N;CHARSET=ISO-8859-1;ENCODING=QUOTED-PRINTABLE:Doe;Andr=E9;;;
            FN;CHARSET=ISO-8859-1;ENCODING=QUOTED-PRINTABLE:Andr=E9 Doe
            EMAIL;INTERNET;PREF:andre@example.com
            PHOTO;ENCODING=BASE64;TYPE=PNG:{{foldedPhoto}}
            END:VCARD
            """, Encoding.ASCII);

        var result = await _service.ImportContactAsync([path]);

        result.Should().NotBeNull();
        result!.Contact.DisplayName.Should().Be("André Doe");
        result.Contact.GivenName.Should().Be("André");
        result.Contact.EmailAddresses.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
        result.PhotoBytes.Should().NotBeNull();
    }

    [Fact]
    public async Task ContactImport_IgnoresRemotePhotoThenUsesEmbeddedPhotoWithWarning()
    {
        var path = WriteFile("photos.vcf", $$"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Photo Person
            PHOTO;VALUE=URI:https://example.com/photo.png
            PHOTO;ENCODING=b;TYPE=PNG:{{OnePixelPng}}
            END:VCARD
            """);

        var result = await _service.ImportContactAsync([path]);

        result.Should().NotBeNull();
        result!.PhotoBytes.Should().Equal(Convert.FromBase64String(OnePixelPng));
        result.HasUnsupportedContent.Should().BeTrue();
    }

    [Fact]
    public async Task ContactImport_UsesFirstValidCardAndReturnsNullForMalformedInput()
    {
        var mixed = WriteFile("mixed.vcf", """
            BEGIN:VCARD
            VERSION:4.0
            NOTE:No editor identity
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:First valid card
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Ignored card
            END:VCARD
            """);
        var malformed = WriteFile("malformed.vcf", "not a vcard");

        (await _service.ImportContactAsync([mixed]))!.Contact.DisplayName.Should().Be("First valid card");
        (await _service.ImportContactAsync([malformed])).Should().BeNull();
    }

    private string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        return path;
    }

    private static string CalendarWithEvent(string eventProperties)
        => $$"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Tests//EN
            BEGIN:VEVENT
            {{eventProperties}}
            END:VEVENT
            END:VCALENDAR
            """;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
