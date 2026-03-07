using System;
using System.IO;
using System.Linq;
using System.Net.Mail;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Validation;

public sealed class CalendarEventComposeResultValidator
{
    public void Validate(CalendarEventComposeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.CalendarId == Guid.Empty)
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationMissingCalendar);

        if (result.AccountId == Guid.Empty)
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationMissingCalendar);

        if (string.IsNullOrWhiteSpace(result.Title))
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationMissingTitle);

        if (result.EndDate <= result.StartDate)
        {
            var message = result.IsAllDay
                ? Translator.CalendarEventCompose_ValidationInvalidAllDayRange
                : Translator.CalendarEventCompose_ValidationInvalidTimeRange;

            throw new CalendarEventComposeValidationException(message);
        }

        var missingAttachments = result.Attachments
            .Where(attachment => string.IsNullOrWhiteSpace(attachment.FilePath) || !File.Exists(attachment.FilePath))
            .Select(attachment => attachment.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingAttachments.Count > 0)
        {
            throw new CalendarEventComposeValidationException(
                string.Format(Translator.CalendarEventCompose_ValidationMissingAttachment, string.Join(", ", missingAttachments)));
        }

        var invalidAttendee = result.Attendees
            .FirstOrDefault(attendee => string.IsNullOrWhiteSpace(attendee.Email) || !IsValidEmailAddress(attendee.Email.Trim()));

        if (invalidAttendee != null)
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationInvalidAttendee);

        var duplicateAttendeeGroups = result.Attendees
            .Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email))
            .GroupBy(attendee => attendee.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateAttendeeGroups != null)
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationInvalidAttendee);
    }

    private static bool IsValidEmailAddress(string address)
    {
        try
        {
            var parsedAddress = new MailAddress(address);
            return parsedAddress.Address.Equals(address, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
