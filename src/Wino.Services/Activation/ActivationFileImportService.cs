using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using SkiaSharp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Services;

public sealed class ActivationFileImportService : IActivationFileImportService
{
    private const int MaximumEmbeddedPhotoBytes = 8 * 1024 * 1024;
    private readonly IWinoLogger _logger;

    public ActivationFileImportService(IWinoLogger logger)
    {
        _logger = logger;
    }

    public async Task<CalendarEventComposeNavigationArgs?> ImportCalendarEventAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        foreach (var filePath in GetExistingFiles(filePaths, ".ics"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                var calendar = Ical.Net.Calendar.Load(content);

                if (calendar?.Events == null)
                    continue;

                foreach (var calendarEvent in calendar.Events)
                {
                    try
                    {
                        var result = TryMapCalendarEvent(calendarEvent);
                        if (result != null)
                            return result;
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.CaptureException(exception, "Map calendar activation event");
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.CaptureException(exception, "Import calendar activation file");
                // Continue to the next activated file. The caller presents one aggregate error.
            }
        }

        return null;
    }

    public async Task<ContactImportDraft?> ImportContactAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        foreach (var filePath in GetExistingFiles(filePaths, ".vcf"))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
                var content = DecodeTextFile(bytes);

                foreach (var card in ExtractVCards(content))
                {
                    try
                    {
                        var result = TryMapVCard(card);
                        if (result != null)
                            return result;
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.CaptureException(exception, "Map contact activation card");
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.CaptureException(exception, "Import contact activation file");
                // Continue to the next activated file. The caller presents one aggregate error.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetExistingFiles(IReadOnlyList<string> filePaths, string extension)
        => (filePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                           File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static CalendarEventComposeNavigationArgs? TryMapCalendarEvent(CalendarEvent calendarEvent)
    {
        if (calendarEvent?.Start == null)
            return null;

        var isAllDay = calendarEvent.IsAllDay || !calendarEvent.Start.HasTime;
        var start = ToLocalDateTime(calendarEvent.Start);
        var end = calendarEvent.End == null ? default : ToLocalDateTime(calendarEvent.End);

        if (start == default)
            return null;

        if (end <= start)
            end = isAllDay ? start.Date.AddDays(1) : start.AddMinutes(30);

        var recurrence = TryMapRecurrence(calendarEvent, out var recurrenceUnsupported);
        var reminderMinutes = TryMapReminder(calendarEvent, out var reminderUnsupported);
        var organizerAddress = NormalizeCalendarAddress(calendarEvent.Organizer?.Value);
        var attendees = calendarEvent.Attendees?
            .Where(attendee => attendee?.Value != null)
            .Select(attendee => new CalendarEventAttendeeDraft(
                string.IsNullOrWhiteSpace(attendee.CommonName)
                    ? NormalizeCalendarAddress(attendee.Value)
                    : attendee.CommonName.Trim(),
                NormalizeCalendarAddress(attendee.Value)))
            .Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email))
            .DistinctBy(attendee => attendee.Email, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var addressHints = attendees.Select(attendee => attendee.Email).ToList();
        if (!string.IsNullOrWhiteSpace(organizerAddress))
            addressHints.Add(organizerAddress);

        var notes = string.IsNullOrWhiteSpace(calendarEvent.Description)
            ? string.Empty
            : WebUtility.HtmlEncode(calendarEvent.Description)
                .Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal);

        return new CalendarEventComposeNavigationArgs
        {
            Title = calendarEvent.Summary ?? string.Empty,
            Location = calendarEvent.Location ?? string.Empty,
            IsAllDay = isAllDay,
            StartDate = start,
            EndDate = end,
            NotesHtml = notes,
            Attendees = attendees,
            Recurrence = recurrence,
            ReminderMinutesBeforeStart = reminderMinutes,
            ShowAs = string.Equals(calendarEvent.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)
                ? CalendarItemShowAs.Free
                : CalendarItemShowAs.Busy,
            AccountAddressHints = addressHints
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RequireCalendarPickerWhenUnresolved = true,
            HasUnsupportedImportContent = recurrenceUnsupported ||
                                          reminderUnsupported ||
                                          HasUnsupportedCalendarFields(calendarEvent)
        };
    }

    private static DateTime ToLocalDateTime(CalDateTime dateTime)
    {
        if (dateTime == null)
            return default;

        if (dateTime.IsFloating || !dateTime.HasTime)
            return DateTime.SpecifyKind(dateTime.Value, DateTimeKind.Unspecified);

        return DateTime.SpecifyKind(dateTime.AsUtc, DateTimeKind.Utc).ToLocalTime();
    }

    private static CalendarEventRecurrenceDraft? TryMapRecurrence(CalendarEvent calendarEvent, out bool unsupported)
    {
        unsupported = false;

        var rule = calendarEvent.RecurrenceRule;
        var recurrenceRulePropertyCount = calendarEvent.Properties.Count(property =>
            string.Equals(property.Name, "RRULE", StringComparison.OrdinalIgnoreCase));
        var hasExtraDates = (calendarEvent.ExceptionDates?.GetAllDates().Any() ?? false) ||
                            (calendarEvent.RecurrenceDates?.GetAllPeriods().Any() ?? false) ||
                            calendarEvent.Properties.Any(property =>
                                string.Equals(property.Name, "EXRULE", StringComparison.OrdinalIgnoreCase));

        if (rule == null)
        {
            unsupported = hasExtraDates;
            return null;
        }

        if (recurrenceRulePropertyCount > 1 || hasExtraDates)
        {
            unsupported = true;
            return null;
        }

        var frequency = rule.Frequency switch
        {
            FrequencyType.Daily => CalendarItemRecurrenceFrequency.Daily,
            FrequencyType.Weekly => CalendarItemRecurrenceFrequency.Weekly,
            FrequencyType.Monthly => CalendarItemRecurrenceFrequency.Monthly,
            FrequencyType.Yearly => CalendarItemRecurrenceFrequency.Yearly,
            _ => (CalendarItemRecurrenceFrequency?)null
        };

        var normalizedInterval = Math.Max(1, rule.Interval);
        var startDay = calendarEvent.Start.Value.DayOfWeek;
        var byDays = rule.ByDay.Select(day => day.DayOfWeek).Distinct().ToList();
        var hasUnrepresentableByDay = frequency switch
        {
            CalendarItemRecurrenceFrequency.Daily => false,
            CalendarItemRecurrenceFrequency.Weekly => byDays.Count > 1 ||
                                                      (byDays.Count == 1 && byDays[0] != startDay),
            _ => byDays.Count > 0
        };
        var hasUnrepresentableUntil = rule.Until != null &&
                                      (calendarEvent.IsAllDay ? rule.Until.HasTime : true);
        var hasUnsupportedParts = frequency == null ||
                                  normalizedInterval > 99 ||
                                  rule.Count.HasValue ||
                                  rule.ByHour.Count > 0 ||
                                  rule.ByMinute.Count > 0 ||
                                  rule.ByMonth.Count > 0 ||
                                  rule.ByMonthDay.Count > 0 ||
                                  rule.BySecond.Count > 0 ||
                                  rule.BySetPosition.Count > 0 ||
                                  rule.ByWeekNo.Count > 0 ||
                                  rule.ByYearDay.Count > 0 ||
                                  rule.ByDay.Any(day => day.Offset.HasValue) ||
                                  hasUnrepresentableByDay ||
                                  hasUnrepresentableUntil;

        if (hasUnsupportedParts)
        {
            unsupported = true;
            return null;
        }

        return new CalendarEventRecurrenceDraft
        {
            Frequency = frequency.Value,
            Interval = normalizedInterval,
            Weekdays = frequency == CalendarItemRecurrenceFrequency.Daily ? byDays : [],
            EndDate = rule.Until == null || rule.Until.Value == default
                ? null
                : ToLocalDateTime(rule.Until)
        };
    }

    private static int? TryMapReminder(CalendarEvent calendarEvent, out bool unsupported)
    {
        unsupported = false;
        var alarms = calendarEvent.Alarms?.Where(alarm => alarm?.Trigger != null).ToList() ?? [];

        if (alarms.Count == 0)
            return null;

        if (alarms.Count > 1)
            unsupported = true;

        foreach (var alarm in alarms)
        {
            if (alarm.Action != AlarmAction.Display ||
                alarm.Properties.Any(property => property.Name is not ("ACTION" or "DESCRIPTION" or "TRIGGER")))
            {
                unsupported = true;
            }

            if (!alarm.Trigger.IsRelative ||
                !alarm.Trigger.Duration.HasValue ||
                alarm.Trigger.Related == TriggerRelation.End)
            {
                unsupported = true;
                continue;
            }

            var totalMinutes = alarm.Trigger.Duration.Value.ToTimeSpanUnspecified().TotalMinutes;
            if (totalMinutes >= 0 || totalMinutes != Math.Truncate(totalMinutes))
            {
                unsupported = true;
                continue;
            }

            var absoluteMinutes = Math.Abs(totalMinutes);
            if (absoluteMinutes > int.MaxValue)
            {
                unsupported = true;
                continue;
            }

            var minutes = (int)absoluteMinutes;
            if (minutes > 0)
                return minutes;
        }

        return null;
    }

    private static bool HasUnsupportedCalendarFields(CalendarEvent calendarEvent)
    {
        string[] unsupportedPropertyNames =
        [
            "ATTACH", "CATEGORIES", "CLASS", "COMMENT", "CONTACT", "GEO", "IMAGE",
            "PRIORITY", "REQUEST-STATUS", "RESOURCES", "STATUS", "URL"
        ];

        return calendarEvent.Properties.Any(property =>
            unsupportedPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
            property.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCalendarAddress(Uri? uri)
    {
        if (uri == null)
            return string.Empty;

        var value = uri.IsAbsoluteUri && string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase)
            ? uri.OriginalString["mailto:".Length..]
            : uri.OriginalString;

        var normalizedValue = Uri.UnescapeDataString(value).Trim();
        var queryIndex = normalizedValue.IndexOf('?');
        if (queryIndex >= 0)
            normalizedValue = normalizedValue[..queryIndex];

        return normalizedValue.Contains('@') ? normalizedValue.ToLowerInvariant() : string.Empty;
    }

    private static ContactImportDraft? TryMapVCard(string cardText)
    {
        var properties = ParseVCardProperties(cardText);
        if (properties.Count == 0)
            return null;

        var contact = new AccountContact();
        var unsupported = HasUnsupportedVCardProperties(properties);

        contact.DisplayName = FirstValue(properties, "FN");
        var nickname = properties.FirstOrDefault(property => property.Name == "NICKNAME");
        contact.Nickname = nickname == null
            ? null
            : SplitEscaped(nickname.StructuredValue, ',').Select(UnescapeVCardText).FirstOrDefault();
        contact.FileAs = FirstValue(properties, "X-FILE-AS") ?? FirstValue(properties, "SORT-STRING");
        contact.JobTitle = FirstValue(properties, "TITLE");
        contact.Profession = FirstValue(properties, "ROLE") ?? FirstValue(properties, "X-PROFESSION");
        contact.OfficeLocation = FirstValue(properties, "X-OFFICE-LOCATION") ??
                                 FirstValue(properties, "X-MS-OFFICE-LOCATION");
        var websites = properties
            .Where(property => property.Name is "URL" or "X-WEBSITE")
            .OrderBy(GetPreference)
            .ToList();
        contact.Website = websites.FirstOrDefault()?.DecodedValue?.Trim();
        unsupported |= websites.Count > 1;
        contact.Notes = FirstValue(properties, "NOTE");

        var name = properties.FirstOrDefault(property => property.Name == "N");
        if (name != null)
        {
            var parts = SplitEscaped(name.StructuredValue, ';');
            contact.Surname = GetPart(parts, 0);
            contact.GivenName = GetPart(parts, 1);
            contact.MiddleName = GetPart(parts, 2);
            contact.HonorificPrefix = GetPart(parts, 3);
            contact.HonorificSuffix = GetPart(parts, 4);

            if (string.IsNullOrWhiteSpace(contact.FileAs))
            {
                var sortAs = name.GetParameterValues("SORT-AS");
                contact.FileAs = sortAs.Count == 0 ? null : string.Join(", ", sortAs);
            }
        }

        var organization = properties.FirstOrDefault(property => property.Name == "ORG");
        if (organization != null)
        {
            var parts = SplitEscaped(organization.StructuredValue, ';');
            contact.CompanyName = GetPart(parts, 0);
            contact.Department = GetPart(parts, 1);
            unsupported |= parts.Skip(2).Any(value => !string.IsNullOrWhiteSpace(value));
        }

        if (string.IsNullOrWhiteSpace(contact.DisplayName))
        {
            contact.DisplayName = string.Join(" ", new[] { contact.GivenName, contact.Surname }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        MapBirthday(FirstValue(properties, "BDAY"), contact, ref unsupported);
        MapEmails(properties, contact, ref unsupported);
        MapPhones(properties, contact);
        MapPostalAddresses(properties, contact, ref unsupported);
        MapImAddresses(properties, contact);
        MapRelations(properties, contact, ref unsupported);

        var photoBytes = MapPhoto(properties, ref unsupported);
        var hasUsefulValue = !string.IsNullOrWhiteSpace(contact.DisplayName) ||
                             !string.IsNullOrWhiteSpace(contact.CompanyName) ||
                             contact.EmailAddresses.Count > 0 ||
                             contact.PhoneNumbers.Count > 0;

        return hasUsefulValue ? new ContactImportDraft(contact, photoBytes, unsupported) : null;
    }

    private static bool HasUnsupportedVCardProperties(List<VCardProperty> properties)
    {
        string[] supportedNames =
        [
            "ADR", "BDAY", "EMAIL", "FN", "IMPP", "N", "NICKNAME", "NOTE", "ORG", "PHOTO",
            "RELATED", "ROLE", "TEL", "TITLE", "URL", "X-AIM", "X-ASSISTANT", "X-CHILD",
            "X-FILE-AS", "X-ICQ", "X-JABBER", "X-MANAGER", "X-MSN", "X-MS-OFFICE-LOCATION",
            "X-OFFICE-LOCATION", "X-PROFESSION", "X-SKYPE", "X-SKYPE-USERNAME", "X-SPOUSE",
            "X-WEBSITE", "X-YAHOO"
        ];
        string[] metadataNames = ["CLIENTPIDMAP", "KIND", "PRODID", "REV", "SOURCE", "SORT-STRING", "UID", "VERSION"];
        string[] singleValueNames =
        [
            "BDAY", "FN", "N", "NICKNAME", "NOTE", "ORG", "ROLE", "SORT-STRING", "TITLE",
            "X-FILE-AS", "X-MS-OFFICE-LOCATION", "X-OFFICE-LOCATION", "X-PROFESSION"
        ];

        return properties.Any(property =>
                   !supportedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                   !metadataNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) ||
               singleValueNames.Any(name => properties.Count(property => property.Name == name) > 1);
    }

    private static void MapBirthday(string? value, AccountContact contact, ref bool unsupported)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var formats = new[] { "yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd'T'HH:mm:ss", "yyyyMMdd'T'HHmmss" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var birthday))
        {
            contact.BirthdayYear = birthday.Year;
            contact.BirthdayMonth = birthday.Month;
            contact.BirthdayDay = birthday.Day;
            return;
        }

        if (DateTime.TryParseExact(value, "--MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out birthday))
        {
            contact.BirthdayMonth = birthday.Month;
            contact.BirthdayDay = birthday.Day;
            return;
        }

        unsupported = true;
    }

    private static void MapEmails(List<VCardProperty> properties, AccountContact contact, ref bool unsupported)
    {
        var emails = properties
            .Where(property => property.Name == "EMAIL" && !string.IsNullOrWhiteSpace(property.DecodedValue))
            .GroupBy(property => property.DecodedValue.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(GetPreference).First())
            .OrderBy(GetPreference)
            .ToList();

        if (emails.Count > 3)
            unsupported = true;

        foreach (var property in emails.Take(3).Select((property, index) => (property, index)))
        {
            contact.EmailAddresses.Add(new ContactEmailAddress
            {
                Address = property.property.DecodedValue.Trim(),
                NormalizedAddress = ContactEmailAddress.Normalize(property.property.DecodedValue),
                Label = GetTypeLabel(property.property),
                Order = property.index,
                IsPrimary = property.index == 0
            });
        }
    }

    private static void MapPhones(List<VCardProperty> properties, AccountContact contact)
    {
        var phones = properties
            .Where(property => property.Name == "TEL" && !string.IsNullOrWhiteSpace(property.DecodedValue))
            .GroupBy(property => property.DecodedValue.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(GetPreference).First())
            .OrderBy(GetPreference)
            .ToList();

        foreach (var property in phones.Select((property, index) => (property, index)))
        {
            contact.PhoneNumbers.Add(new ContactPhoneNumber
            {
                Number = property.property.DecodedValue.Trim(),
                Kind = HasType(property.property, "cell") || HasType(property.property, "mobile")
                    ? ContactPhoneKind.Mobile
                    : HasType(property.property, "work")
                        ? ContactPhoneKind.Work
                        : ContactPhoneKind.Home,
                Order = property.index,
                IsPrimary = property.index == 0
            });
        }
    }

    private static void MapPostalAddresses(List<VCardProperty> properties, AccountContact contact, ref bool unsupported)
    {
        var usedKinds = new HashSet<ContactPostalAddressKind>();

        foreach (var property in properties.Where(property => property.Name == "ADR").OrderBy(GetPreference))
        {
            var kind = HasType(property, "home")
                ? ContactPostalAddressKind.Home
                : HasType(property, "work")
                    ? ContactPostalAddressKind.Business
                    : ContactPostalAddressKind.Other;

            if (!usedKinds.Add(kind))
            {
                unsupported = true;
                continue;
            }

            var parts = SplitEscaped(property.StructuredValue, ';');
            contact.PostalAddresses.Add(new ContactPostalAddress
            {
                Kind = kind,
                PostOfficeBox = GetPart(parts, 0),
                Street = string.Join(" ", new[] { GetPart(parts, 1), GetPart(parts, 2) }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                City = GetPart(parts, 3),
                Region = GetPart(parts, 4),
                PostalCode = GetPart(parts, 5),
                Country = GetPart(parts, 6)
            });
        }
    }

    private static void MapImAddresses(List<VCardProperty> properties, AccountContact contact)
    {
        string[] supportedImNames =
        [
            "IMPP", "X-AIM", "X-ICQ", "X-JABBER", "X-MSN", "X-SKYPE", "X-SKYPE-USERNAME", "X-YAHOO"
        ];
        var imAddresses = properties
            .Where(property => supportedImNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(property.DecodedValue))
            .GroupBy(property => GetImKey(property), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(GetPreference).First())
            .OrderBy(GetPreference);

        foreach (var property in imAddresses)
        {
            var value = property.DecodedValue.Trim();
            var separator = value.IndexOf(':');
            var protocol = separator > 0
                ? value[..separator]
                : property.Name == "IMPP"
                    ? GetTypeLabel(property)
                    : property.Name[2..].Replace("-USERNAME", string.Empty, StringComparison.OrdinalIgnoreCase);
            contact.ImAddresses.Add(new ContactImAddress
            {
                Protocol = protocol,
                Address = separator > 0 ? value[(separator + 1)..] : value,
                Order = contact.ImAddresses.Count
            });
        }
    }

    private static string GetImKey(VCardProperty property)
    {
        var value = property.DecodedValue.Trim();
        if (value.Contains(':'))
            return value;

        var protocol = property.Name == "IMPP"
            ? GetTypeLabel(property)
            : property.Name[2..].Replace("-USERNAME", string.Empty, StringComparison.OrdinalIgnoreCase);

        return $"{protocol}:{value}";
    }

    private static void MapRelations(List<VCardProperty> properties, AccountContact contact, ref bool unsupported)
    {
        var relationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties.Where(property => property.Name is "RELATED" or "X-MANAGER" or "X-ASSISTANT" or "X-SPOUSE" or "X-CHILD"))
        {
            var kind = property.Name switch
            {
                "X-MANAGER" => ContactRelationKind.Manager,
                "X-ASSISTANT" => ContactRelationKind.Assistant,
                "X-SPOUSE" => ContactRelationKind.Spouse,
                "X-CHILD" => ContactRelationKind.Child,
                _ when HasType(property, "manager") => ContactRelationKind.Manager,
                _ when HasType(property, "assistant") => ContactRelationKind.Assistant,
                _ when HasType(property, "spouse") => ContactRelationKind.Spouse,
                _ when HasType(property, "child") => ContactRelationKind.Child,
                _ => (ContactRelationKind?)null
            };

            if (kind == null || string.IsNullOrWhiteSpace(property.DecodedValue))
            {
                unsupported = true;
                continue;
            }

            if (!relationKeys.Add($"{kind.Value}:{property.DecodedValue.Trim()}"))
                continue;

            contact.Relations.Add(new ContactRelation
            {
                Kind = kind.Value,
                Name = property.DecodedValue.Trim(),
                Order = contact.Relations.Count
            });
        }
    }

    private static byte[]? MapPhoto(List<VCardProperty> properties, ref bool unsupported)
    {
        foreach (var photo in properties.Where(property => property.Name == "PHOTO" && !string.IsNullOrWhiteSpace(property.RawValue)))
        {
            try
            {
                byte[]? bytes;

                if (photo.RawValue.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = photo.RawValue.IndexOf(',');
                    if (comma <= 0)
                    {
                        unsupported = true;
                        continue;
                    }

                    bytes = photo.RawValue[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                        ? Convert.FromBase64String(RemoveWhitespace(photo.RawValue[(comma + 1)..]))
                        : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(photo.RawValue[(comma + 1)..]));
                }
                else if (photo.HasParameterValue("ENCODING", "b") || photo.HasParameterValue("ENCODING", "base64"))
                {
                    bytes = Convert.FromBase64String(RemoveWhitespace(photo.RawValue));
                }
                else
                {
                    // Do not fetch remote images during file activation.
                    unsupported = true;
                    continue;
                }

                if (bytes is not { Length: > 0 } || bytes.Length > MaximumEmbeddedPhotoBytes)
                {
                    unsupported = true;
                    continue;
                }

                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null)
                {
                    unsupported = true;
                    continue;
                }

                return bytes;
            }
            catch
            {
                unsupported = true;
            }
        }

        return null;
    }

    private static string DecodeTextFile(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static IEnumerable<string> ExtractVCards(string content)
    {
        var lines = UnfoldLines(content);
        var card = new List<string>();
        var inCard = false;

        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                card.Clear();
                inCard = true;
                continue;
            }

            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (inCard)
                    yield return string.Join("\r\n", card);

                card.Clear();
                inCard = false;
                continue;
            }

            if (inCard)
                card.Add(line);
        }
    }

    private static List<string> UnfoldLines(string content)
    {
        var physicalLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var logicalLines = new List<string>();

        foreach (var physicalLine in physicalLines)
        {
            if (logicalLines.Count > 0 && logicalLines[^1].EndsWith('=') &&
                logicalLines[^1].Contains("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            {
                var continuation = physicalLine.StartsWith(' ') || physicalLine.StartsWith('\t')
                    ? physicalLine[1..]
                    : physicalLine;
                logicalLines[^1] = logicalLines[^1][..^1] + continuation;
                continue;
            }

            if (logicalLines.Count > 0 && (physicalLine.StartsWith(' ') || physicalLine.StartsWith('\t')))
            {
                logicalLines[^1] += physicalLine[1..];
                continue;
            }

            if (logicalLines.Count > 0 &&
                logicalLines[^1].StartsWith("PHOTO;", StringComparison.OrdinalIgnoreCase) &&
                logicalLines[^1].Contains("ENCODING=", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(physicalLine) &&
                !physicalLine.Contains(':') &&
                !physicalLine.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                logicalLines[^1] += physicalLine.Trim();
                continue;
            }

            logicalLines.Add(physicalLine);
        }

        return logicalLines;
    }

    private static List<VCardProperty> ParseVCardProperties(string cardText)
    {
        var result = new List<VCardProperty>();

        foreach (var line in cardText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = FindUnescapedSeparator(line, ':');
            if (separator <= 0)
                continue;

            var header = line[..separator];
            var rawValue = line[(separator + 1)..];
            var headerParts = SplitHeader(header);
            if (headerParts.Count == 0)
                continue;

            var rawName = headerParts[0];
            var property = new VCardProperty
            {
                Name = rawName[(rawName.LastIndexOf('.') + 1)..].ToUpperInvariant(),
                RawValue = rawValue
            };

            foreach (var part in headerParts.Skip(1))
            {
                var equals = part.IndexOf('=');
                if (equals > 0)
                {
                    var key = part[..equals].Trim().ToUpperInvariant();
                    var values = part[(equals + 1)..].Trim(' ', '"').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    property.Parameters[key] = values.ToList();
                }
                else if (!string.IsNullOrWhiteSpace(part))
                {
                    property.Parameters.TryAdd("TYPE", []);
                    property.Parameters["TYPE"].Add(part.Trim());
                }
            }

            property.StructuredValue = DecodeVCardEncodedValue(property);
            property.DecodedValue = UnescapeVCardText(property.StructuredValue);
            result.Add(property);
        }

        return result;
    }

    private static string DecodeVCardEncodedValue(VCardProperty property)
    {
        var value = property.RawValue;

        if (property.HasParameterValue("ENCODING", "quoted-printable"))
        {
            var bytes = DecodeQuotedPrintable(value);
            var charset = property.GetParameterValues("CHARSET").FirstOrDefault();

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                value = string.IsNullOrWhiteSpace(charset)
                    ? Encoding.UTF8.GetString(bytes)
                    : Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch
            {
                value = Encoding.UTF8.GetString(bytes);
            }
        }

        return value;
    }

    private static byte[] DecodeQuotedPrintable(string value)
    {
        using var stream = new MemoryStream();

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '=' && index + 2 < value.Length &&
                byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
            {
                stream.WriteByte(decoded);
                index += 2;
            }
            else
            {
                stream.WriteByte((byte)value[index]);
            }
        }

        return stream.ToArray();
    }

    private static string UnescapeVCardText(string value)
    {
        var result = new StringBuilder(value.Length);
        var escaped = false;

        foreach (var character in value)
        {
            if (escaped)
            {
                result.Append(character is 'n' or 'N' ? '\n' : character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else
            {
                result.Append(character);
            }
        }

        if (escaped)
            result.Append('\\');

        return result.ToString();
    }

    private static List<string> SplitEscaped(string value, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == separator)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static int FindUnescapedSeparator(string value, char separator)
    {
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value[index] == '\\')
            {
                escaped = true;
                continue;
            }

            if (value[index] == separator)
                return index;
        }

        return -1;
    }

    private static List<string> SplitHeader(string header)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in header)
        {
            if (character == '"')
                quoted = !quoted;

            if (character == ';' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static string? FirstValue(List<VCardProperty> properties, string name)
        => properties.FirstOrDefault(property => property.Name == name)?.DecodedValue?.Trim();

    private static string GetPart(IReadOnlyList<string> parts, int index)
        => index < parts.Count ? parts[index] : string.Empty;

    private static int GetPreference(VCardProperty property)
    {
        var value = property.GetParameterValues("PREF").FirstOrDefault();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preference))
            return preference;

        return property.GetParameterValues("TYPE").Any(type => type.Equals("pref", StringComparison.OrdinalIgnoreCase)) ? 1 : int.MaxValue;
    }

    private static string GetTypeLabel(VCardProperty property)
        => property.GetParameterValues("TYPE").FirstOrDefault(type => !type.Equals("pref", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    private static bool HasType(VCardProperty property, string value)
        => property.GetParameterValues("TYPE").Any(type => type.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string RemoveWhitespace(string value)
        => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private sealed class VCardProperty
    {
        public string Name { get; init; } = string.Empty;
        public string RawValue { get; init; } = string.Empty;
        public string StructuredValue { get; set; } = string.Empty;
        public string DecodedValue { get; set; } = string.Empty;
        public Dictionary<string, List<string>> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetParameterValues(string name)
            => Parameters.TryGetValue(name, out var values) ? values : [];

        public bool HasParameterValue(string name, string value)
            => GetParameterValues(name).Any(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
