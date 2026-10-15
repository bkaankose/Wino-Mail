#nullable enable
using System;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// How a calendar page turns its navigation parameter into a range load (Windows CalendarPage.OnNavigatedTo), shared by
/// the Mac, iPhone, iPad and Android calendar pages: the anchor date comes from <see cref="CalendarPageNavigationArgs"/>
/// unless the default navigation is requested, and the pending target and forced reload travel with the request.
/// </summary>
public static class CalendarPageActivation
{
    /// <summary>The range load for a calendar page navigated to with <paramref name="parameter"/>.</summary>
    public static LoadCalendarMessage CreateLoadMessage(object? parameter, CalendarDisplayType displayType, DateOnly today)
    {
        var anchorDate = today;
        CalendarItemTarget? pendingTarget = null;
        bool forceReload = false;
        if (parameter is CalendarPageNavigationArgs args)
        {
            if (!args.RequestDefaultNavigation) anchorDate = DateOnly.FromDateTime(args.NavigationDate.Date);
            pendingTarget = args.PendingTarget;
            forceReload = args.ForceReload;
        }

        return new LoadCalendarMessage(new CalendarDisplayRequest(displayType, anchorDate), forceReload, pendingTarget);
    }

    /// <summary>A search result opens on its own date with the event as the pending target (Mac and iPad search).</summary>
    public static LoadCalendarMessage CreateSearchResultMessage(CalendarItem item, CalendarDisplayType displayType)
    {
        ArgumentNullException.ThrowIfNull(item);
        var request = new CalendarDisplayRequest(displayType, DateOnly.FromDateTime(item.LocalStartDate));
        return new LoadCalendarMessage(request, false, new CalendarItemTarget(item, CalendarEventTargetType.Single));
    }
}
