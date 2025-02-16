using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Graph.Models;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Misc;

namespace Wino.Core.Extensions
{
    public static class OutlookIntegratorExtensions
    {
        public static MailItemFolder GetLocalFolder(this MailFolder nativeFolder, Guid accountId)
        {
            return new MailItemFolder()
            {
                Id = Guid.NewGuid(),
                FolderName = nativeFolder.DisplayName,
                RemoteFolderId = nativeFolder.Id,
                ParentRemoteFolderId = nativeFolder.ParentFolderId,
                IsSynchronizationEnabled = true,
                MailAccountId = accountId,
                IsHidden = nativeFolder.IsHidden.GetValueOrDefault()
            };
        }

        public static bool GetIsDraft(this Message message)
            => message != null && message.IsDraft.GetValueOrDefault();

        public static bool GetIsRead(this Message message)
            => message != null && message.IsRead.GetValueOrDefault();

        public static bool GetIsFocused(this Message message)
            => message?.InferenceClassification != null && message.InferenceClassification.Value == InferenceClassificationType.Focused;

        public static bool GetIsFlagged(this Message message)
            => message?.Flag?.FlagStatus != null && message.Flag.FlagStatus == FollowupFlagStatus.Flagged;

        public static MailCopy AsMailCopy(this Message outlookMessage)
        {
            bool isDraft = GetIsDraft(outlookMessage);

            var mailCopy = new MailCopy()
            {
                MessageId = outlookMessage.InternetMessageId,
                IsFlagged = GetIsFlagged(outlookMessage),
                IsFocused = GetIsFocused(outlookMessage),
                Importance = !outlookMessage.Importance.HasValue ? MailImportance.Normal : (MailImportance)outlookMessage.Importance.Value,
                IsRead = GetIsRead(outlookMessage),
                IsDraft = isDraft,
                CreationDate = outlookMessage.ReceivedDateTime.GetValueOrDefault().DateTime,
                HasAttachments = outlookMessage.HasAttachments.GetValueOrDefault(),
                PreviewText = outlookMessage.BodyPreview,
                Id = outlookMessage.Id,
                ThreadId = outlookMessage.ConversationId,
                FromName = outlookMessage.From?.EmailAddress?.Name,
                FromAddress = outlookMessage.From?.EmailAddress?.Address,
                Subject = outlookMessage.Subject,
                FileId = Guid.NewGuid()
            };

            if (mailCopy.IsDraft)
                mailCopy.DraftId = mailCopy.ThreadId;

            return mailCopy;
        }

        public static Message AsOutlookMessage(this MimeMessage mime, bool includeInternetHeaders)
        {
            var fromAddress = GetRecipients(mime.From).ElementAt(0);
            var toAddresses = GetRecipients(mime.To).ToList();
            var ccAddresses = GetRecipients(mime.Cc).ToList();
            var bccAddresses = GetRecipients(mime.Bcc).ToList();
            var replyToAddresses = GetRecipients(mime.ReplyTo).ToList();

            var message = new Message()
            {
                Subject = mime.Subject,
                Importance = GetImportance(mime.Importance),
                Body = new ItemBody() { ContentType = BodyType.Html, Content = mime.HtmlBody },
                IsDraft = false,
                IsRead = true, // Sent messages are always read.
                ToRecipients = toAddresses,
                CcRecipients = ccAddresses,
                BccRecipients = bccAddresses,
                From = fromAddress,
                InternetMessageId = GetProperId(mime.MessageId),
                ReplyTo = replyToAddresses,
                Attachments = []
            };

            // Headers are only included when creating the draft.
            // When sending, they are not included. Graph will throw an error.

            if (includeInternetHeaders)
            {
                message.InternetMessageHeaders = GetHeaderList(mime);
            }


            return message;
        }

        public static AccountCalendar AsCalendar(this Calendar outlookCalendar, MailAccount assignedAccount)
        {
            var calendar = new AccountCalendar()
            {
                AccountId = assignedAccount.Id,
                Id = Guid.NewGuid(),
                RemoteCalendarId = outlookCalendar.Id,
                IsPrimary = outlookCalendar.IsDefaultCalendar.GetValueOrDefault(),
                Name = outlookCalendar.Name,
                IsExtended = true,
            };

            // Colors:
            // Bg must be present. Generate flat one if doesn't exists.
            // Text doesnt exists for Outlook.

            calendar.BackgroundColorHex = string.IsNullOrEmpty(outlookCalendar.HexColor) ? ColorHelpers.GenerateFlatColorHex() : outlookCalendar.HexColor;
            calendar.TextColorHex = "#000000";

            return calendar;
        }

        private static string GetRfc5545DayOfWeek(DayOfWeekObject dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeekObject.Monday => "MO",
                DayOfWeekObject.Tuesday => "TU",
                DayOfWeekObject.Wednesday => "WE",
                DayOfWeekObject.Thursday => "TH",
                DayOfWeekObject.Friday => "FR",
                DayOfWeekObject.Saturday => "SA",
                DayOfWeekObject.Sunday => "SU",
                _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, null)
            };
        }

        public static string ToRfc5545RecurrenceString(this PatternedRecurrence recurrence)
        {
            if (recurrence == null || recurrence.Pattern == null)
                throw new ArgumentNullException(nameof(recurrence), "PatternedRecurrence or its Pattern cannot be null.");

            var ruleBuilder = new StringBuilder("RRULE:");
            var pattern = recurrence.Pattern;

            // Frequency
            switch (pattern.Type)
            {
                case RecurrencePatternType.Daily:
                    ruleBuilder.Append("FREQ=DAILY;");
                    break;
                case RecurrencePatternType.Weekly:
                    ruleBuilder.Append("FREQ=WEEKLY;");
                    break;
                case RecurrencePatternType.AbsoluteMonthly:
                    ruleBuilder.Append("FREQ=MONTHLY;");
                    break;
                case RecurrencePatternType.AbsoluteYearly:
                    ruleBuilder.Append("FREQ=YEARLY;");
                    break;
                case RecurrencePatternType.RelativeMonthly:
                    ruleBuilder.Append("FREQ=MONTHLY;");
                    break;
                case RecurrencePatternType.RelativeYearly:
                    ruleBuilder.Append("FREQ=YEARLY;");
                    break;
                default:
                    throw new NotSupportedException($"Unsupported recurrence pattern type: {pattern.Type}");
            }

            // Interval
            if (pattern.Interval > 0)
                ruleBuilder.Append($"INTERVAL={pattern.Interval};");

            // Days of Week
            if (pattern.DaysOfWeek?.Any() == true)
            {
                var days = string.Join(",", pattern.DaysOfWeek.Select(day => day.ToString().ToUpperInvariant().Substring(0, 2)));
                ruleBuilder.Append($"BYDAY={days};");
            }

            // Day of Month (BYMONTHDAY)
            if (pattern.Type == RecurrencePatternType.AbsoluteMonthly || pattern.Type == RecurrencePatternType.AbsoluteYearly)
            {
                if (pattern.DayOfMonth <= 0)
                    throw new ArgumentException("DayOfMonth must be greater than 0 for absoluteMonthly or absoluteYearly patterns.");

                ruleBuilder.Append($"BYMONTHDAY={pattern.DayOfMonth};");
            }

            // Month (BYMONTH)
            if (pattern.Type == RecurrencePatternType.AbsoluteYearly || pattern.Type == RecurrencePatternType.RelativeYearly)
            {
                if (pattern.Month <= 0)
                    throw new ArgumentException("Month must be greater than 0 for absoluteYearly or relativeYearly patterns.");

                ruleBuilder.Append($"BYMONTH={pattern.Month};");
            }

            // Count or Until
            if (recurrence.Range != null)
            {
                if (recurrence.Range.Type == RecurrenceRangeType.EndDate && recurrence.Range.EndDate != null)
                {
                    ruleBuilder.Append($"UNTIL={recurrence.Range.EndDate.Value:yyyyMMddTHHmmssZ};");
                }
                else if (recurrence.Range.Type == RecurrenceRangeType.Numbered && recurrence.Range.NumberOfOccurrences.HasValue)
                {
                    ruleBuilder.Append($"COUNT={recurrence.Range.NumberOfOccurrences.Value};");
                }
            }

            // Remove trailing semicolon
            return ruleBuilder.ToString().TrimEnd(';');
        }

        public static DateTimeOffset GetDateTimeOffsetFromDateTimeTimeZone(DateTimeTimeZone dateTimeTimeZone)
        {
            if (dateTimeTimeZone == null || string.IsNullOrEmpty(dateTimeTimeZone.DateTime) || string.IsNullOrEmpty(dateTimeTimeZone.TimeZone))
            {
                throw new ArgumentException("DateTimeTimeZone is null or empty.");
            }

            try
            {
                // Parse the DateTime string
                if (DateTime.TryParse(dateTimeTimeZone.DateTime, out DateTime parsedDateTime))
                {
                    // Get TimeZoneInfo to get the offset
                    TimeZoneInfo timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(dateTimeTimeZone.TimeZone);
                    TimeSpan offset = timeZoneInfo.GetUtcOffset(parsedDateTime);
                    return new DateTimeOffset(parsedDateTime, offset);
                }
                else
                    throw new ArgumentException("DateTime string is not in a valid format.");
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static AttendeeStatus GetAttendeeStatus(ResponseType? responseType)
        {
            return responseType switch
            {
                ResponseType.None => AttendeeStatus.NeedsAction,
                ResponseType.NotResponded => AttendeeStatus.NeedsAction,
                ResponseType.Organizer => AttendeeStatus.Accepted,
                ResponseType.TentativelyAccepted => AttendeeStatus.Tentative,
                ResponseType.Accepted => AttendeeStatus.Accepted,
                ResponseType.Declined => AttendeeStatus.Declined,
                _ => AttendeeStatus.NeedsAction
            };
        }

        public static CalendarEventAttendee CreateAttendee(this Attendee attendee, Guid calendarItemId)
        {
            bool isOrganizer = attendee?.Status?.Response == ResponseType.Organizer;

            var eventAttendee = new CalendarEventAttendee()
            {
                CalendarItemId = calendarItemId,
                Id = Guid.NewGuid(),
                Email = attendee.EmailAddress?.Address,
                Name = attendee.EmailAddress?.Name,
                AttendenceStatus = GetAttendeeStatus(attendee.Status.Response),
                IsOrganizer = isOrganizer,
                IsOptionalAttendee = attendee.Type == AttendeeType.Optional,
            };

            return eventAttendee;
        }

        #region Mime to Outlook Message Helpers

        private static IEnumerable<Recipient> GetRecipients(this InternetAddressList internetAddresses)
        {
            foreach (var address in internetAddresses)
            {
                if (address is MailboxAddress mailboxAddress)
                    yield return new Recipient() { EmailAddress = new EmailAddress() { Address = mailboxAddress.Address, Name = mailboxAddress.Name } };
                else if (address is GroupAddress groupAddress)
                {
                    // TODO: Group addresses are not directly supported.
                    // It'll be individually added.

                    foreach (var mailbox in groupAddress.Members)
                        if (mailbox is MailboxAddress groupMemberMailAddress)
                            yield return new Recipient() { EmailAddress = new EmailAddress() { Address = groupMemberMailAddress.Address, Name = groupMemberMailAddress.Name } };
                }
            }
        }

        private static Importance? GetImportance(MessageImportance importance)
        {
            return importance switch
            {
                MessageImportance.Low => Importance.Low,
                MessageImportance.Normal => Importance.Normal,
                MessageImportance.High => Importance.High,
                _ => null
            };
        }

        private static List<InternetMessageHeader> GetHeaderList(this MimeMessage mime)
        {
            // Graph API only allows max of 5 headers.
            // Here we'll try to ignore some headers that are not neccessary.
            // Outlook API will generate them automatically.

            // Some headers also require to start with X- or x-.

            string[] headersToIgnore = ["Date", "To", "Cc", "Bcc", "MIME-Version", "From", "Subject", "Message-Id"];
            string[] headersToModify = ["In-Reply-To", "Reply-To", "References", "Thread-Topic"];

            var headers = new List<InternetMessageHeader>();

            int includedHeaderCount = 0;

            foreach (var header in mime.Headers)
            {
                if (!headersToIgnore.Contains(header.Field))
                {
                    var headerName = headersToModify.Contains(header.Field) ? $"X-{header.Field}" : header.Field;

                    // No header value should exceed 995 characters.
                    var headerValue = header.Value.Length >= 995 ? header.Value.Substring(0, 995) : header.Value;

                    headers.Add(new InternetMessageHeader() { Name = headerName, Value = headerValue });
                    includedHeaderCount++;
                }

                if (includedHeaderCount >= 5) break;
            }

            return headers;
        }

        private static string GetProperId(string id)
        {
            // Outlook requires some identifiers to start with "X-" or "x-".
            if (string.IsNullOrEmpty(id)) return string.Empty;

            if (!id.StartsWith("x-") || !id.StartsWith("X-"))
                return $"X-{id}";

            return id;
        }


        #endregion
    }
}
