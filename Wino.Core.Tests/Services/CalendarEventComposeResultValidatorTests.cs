using FluentAssertions;
using System.IO;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Validation;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CalendarEventComposeResultValidatorTests
{
    private readonly CalendarEventComposeResultValidator _validator = new();

    [Fact]
    public void Validate_WhenResultIsValid_DoesNotThrow()
    {
        var tempFilePath = Path.GetTempFileName();

        try
        {
            var result = CreateValidResult();
            result.Attachments.Add(new CalendarEventComposeAttachmentDraft
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(tempFilePath),
                FilePath = tempFilePath,
                FileExtension = ".tmp",
                Size = 12
            });

            Action act = () => _validator.Validate(result);

            act.Should().NotThrow();
        }
        finally
        {
            File.Delete(tempFilePath);
        }
    }

    [Fact]
    public void Validate_WhenEndDateIsBeforeStartDate_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.EndDate = result.StartDate.AddMinutes(-30);

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(Translator.CalendarEventCompose_ValidationInvalidTimeRange);
    }

    [Fact]
    public void Validate_WhenAllDayEndDateMatchesStartDate_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.IsAllDay = true;
        result.EndDate = result.StartDate;

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(Translator.CalendarEventCompose_ValidationInvalidAllDayRange);
    }

    [Fact]
    public void Validate_WhenAttachmentDoesNotExist_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.Attachments.Add(new CalendarEventComposeAttachmentDraft
        {
            Id = Guid.NewGuid(),
            FileName = "missing.txt",
            FilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt"),
            FileExtension = ".txt",
            Size = 42
        });

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(string.Format(Translator.CalendarEventCompose_ValidationMissingAttachment, "missing.txt"));
    }

    [Fact]
    public void Validate_WhenAttendeeEmailIsInvalid_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.Attendees.Add(new CalendarEventAttendee
        {
            Id = Guid.NewGuid(),
            CalendarItemId = Guid.Empty,
            Email = "not-an-email"
        });

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(Translator.CalendarEventCompose_ValidationInvalidAttendee);
    }

    [Fact]
    public void Validate_WhenAttendeeEmailIsDuplicated_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.Attendees.Add(new CalendarEventAttendee
        {
            Id = Guid.NewGuid(),
            CalendarItemId = Guid.Empty,
            Email = "person@example.com"
        });
        result.Attendees.Add(new CalendarEventAttendee
        {
            Id = Guid.NewGuid(),
            CalendarItemId = Guid.Empty,
            Email = "PERSON@example.com"
        });

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(Translator.CalendarEventCompose_ValidationInvalidAttendee);
    }

    [Fact]
    public void Validate_WhenCalendarIdIsMissing_ThrowsValidationException()
    {
        var result = CreateValidResult();
        result.CalendarId = Guid.Empty;

        Action act = () => _validator.Validate(result);

        act.Should()
            .Throw<CalendarEventComposeValidationException>()
            .WithMessage(Translator.CalendarEventCompose_ValidationMissingCalendar);
    }

    private static CalendarEventComposeResult CreateValidResult()
    {
        return new CalendarEventComposeResult
        {
            CalendarId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Title = "Design review",
            StartDate = new DateTime(2026, 3, 7, 13, 30, 0),
            EndDate = new DateTime(2026, 3, 7, 14, 0, 0),
            TimeZoneId = TimeZoneInfo.Local.Id,
            SelectedReminders =
            [
                new Reminder
                {
                    Id = Guid.NewGuid(),
                    DurationInSeconds = 900
                }
            ]
        };
    }
}
