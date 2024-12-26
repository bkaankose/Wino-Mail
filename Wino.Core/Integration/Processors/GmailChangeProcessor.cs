using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Apis.Calendar.v3.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Services;
using CalendarEventAttendee = Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee;
using CalendarItem = Wino.Core.Domain.Entities.Calendar.CalendarItem;

namespace Wino.Core.Integration.Processors
{
    public class GmailChangeProcessor : DefaultChangeProcessor, IGmailChangeProcessor
    {
        public GmailChangeProcessor(IDatabaseService databaseService,
                                    IFolderService folderService,
                                    IMailService mailService,
                                    ICalendarService calendarService,
                                    IAccountService accountService,
                                    IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
        {

        }

        public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);

        public Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
            => MailService.CreateAssignmentAsync(accountId, mailCopyId, remoteFolderId);

        public async Task<CalendarItem> CreateCalendarItemAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
        {
            var calendarItem = new CalendarItem()
            {
                CalendarId = assignedCalendar.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Description = calendarEvent.Description,
                StartTime = GoogleIntegratorExtensions.GetEventStartDateTimeOffset(calendarEvent) ?? throw new Exception("Event without a start time."),
                DurationInMinutes = GoogleIntegratorExtensions.GetEventDurationInMinutes(calendarEvent) ?? throw new Exception("Event without a duration."),
                Id = Guid.NewGuid(),
                Location = calendarEvent.Location,
                Recurrence = GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent),
                Status = GetStatus(calendarEvent.Status),
                Title = calendarEvent.Summary,
                UpdatedAt = DateTimeOffset.UtcNow,
                Visibility = GetVisibility(calendarEvent.Visibility),
            };

            // TODO: There are some edge cases with cancellation here.
            CalendarItemStatus GetStatus(string status)
            {
                return status switch
                {
                    "confirmed" => CalendarItemStatus.Confirmed,
                    "tentative" => CalendarItemStatus.Tentative,
                    "cancelled" => CalendarItemStatus.Cancelled,
                    _ => CalendarItemStatus.Confirmed
                };
            }

            CalendarItemVisibility GetVisibility(string visibility)
            {
                /// Visibility of the event. Optional. Possible values are:   - "default" - Uses the default visibility for
                /// events on the calendar. This is the default value.  - "public" - The event is public and event details are
                /// visible to all readers of the calendar.  - "private" - The event is private and only event attendees may
                /// view event details.  - "confidential" - The event is private. This value is provided for compatibility
                /// reasons.

                return visibility switch
                {
                    "default" => CalendarItemVisibility.Default,
                    "public" => CalendarItemVisibility.Public,
                    "private" => CalendarItemVisibility.Private,
                    "confidential" => CalendarItemVisibility.Confidential,
                    _ => CalendarItemVisibility.Default
                };
            }

            // Attendees
            var attendees = new List<CalendarEventAttendee>();

            if (calendarEvent.Attendees == null)
            {
                // Self-only event.

                attendees.Add(new CalendarEventAttendee()
                {
                    CalendarItemId = calendarItem.Id,
                    IsOrganizer = true,
                    Email = organizerAccount.Address,
                    Name = organizerAccount.SenderName,
                    AttendenceStatus = AttendeeStatus.Accepted,
                    Id = Guid.NewGuid(),
                    IsOptionalAttendee = false,
                });
            }
            else
            {
                foreach (var attendee in calendarEvent.Attendees)
                {
                    if (attendee.Self == true)
                    {
                        // TODO: 
                    }
                    else if (!string.IsNullOrEmpty(attendee.Email))
                    {
                        AttendeeStatus GetAttendenceStatus(string responseStatus)
                        {
                            return responseStatus switch
                            {
                                "accepted" => AttendeeStatus.Accepted,
                                "declined" => AttendeeStatus.Declined,
                                "tentative" => AttendeeStatus.Tentative,
                                "needsAction" => AttendeeStatus.NeedsAction,
                                _ => AttendeeStatus.NeedsAction
                            };
                        }

                        var eventAttendee = new CalendarEventAttendee()
                        {
                            CalendarItemId = calendarItem.Id,
                            IsOrganizer = attendee.Organizer ?? false,
                            Comment = attendee.Comment,
                            Email = attendee.Email,
                            Name = attendee.DisplayName,
                            AttendenceStatus = GetAttendenceStatus(attendee.ResponseStatus),
                            Id = Guid.NewGuid(),
                            IsOptionalAttendee = attendee.Optional ?? false,
                        };

                        attendees.Add(eventAttendee);
                    }
                }
            }


            await CalendarService.CreateNewCalendarItemAsync(calendarItem, attendees);

            return calendarItem;
        }
    }
}
