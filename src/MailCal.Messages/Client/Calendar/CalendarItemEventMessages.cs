using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.Client.Calendar;

public record CalendarItemAdded(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server);
public record CalendarItemUpdated(CalendarItem CalendarItem, EntityUpdateSource Source);
public record CalendarItemDeleted(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server);

/// <summary>
/// Sent after a synchronizer finished executing queued calendar requests, whether they succeeded or failed.
/// Ends the optimistic busy state that <see cref="EntityUpdateSource.ClientUpdated"/> started for these local item ids.
/// </summary>
public record CalendarItemOperationsCompleted(Guid AccountId, IReadOnlyCollection<Guid> CalendarItemIds);
