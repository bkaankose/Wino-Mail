using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Enums;
/// <summary>
/// Represents the response status of an attendee to a calendar event
/// </summary>
public enum AttendeeResponseStatus
{
    /// <summary>
    /// The attendee has not responded to the invitation
    /// </summary>
    NeedsAction = 0,

    /// <summary>
    /// The attendee has accepted the invitation
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// The attendee has declined the invitation
    /// </summary>
    Declined = 2,

    /// <summary>
    /// The attendee has tentatively accepted the invitation
    /// </summary>
    Tentative = 3
}
