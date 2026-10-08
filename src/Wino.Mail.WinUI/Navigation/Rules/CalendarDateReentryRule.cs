#nullable enable

using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Mail.WinUI.Navigation.Rules;

/// <summary>
/// Moving the calendar to another date must never create a second calendar surface.
/// When the calendar is on screen it just reloads; when it is one step back (behind the
/// event details page) the existing instance is reused by going back.
/// </summary>
public sealed class CalendarDateReentryRule(IStatePersistanceService statePersistanceService) : INavigationReentryRule
{
    public WinoPage Page => WinoPage.CalendarPage;

    public ReentryDecision Evaluate(NavigationContext context)
    {
        if (context.Parameter is not CalendarPageNavigationArgs calendarArgs)
            return ReentryDecision.Navigate();

        if (context.IsTargetActive)
            return ReentryDecision.HandleInPlace(() => SendLoadCalendarMessage(calendarArgs));

        if (context.IsTargetOnTopOfBackStack)
            return ReentryDecision.ReuseBackStackEntry(() => SendLoadCalendarMessage(calendarArgs));

        return ReentryDecision.Navigate();
    }

    private void SendLoadCalendarMessage(CalendarPageNavigationArgs args)
    {
        var targetDate = args.RequestDefaultNavigation
            ? DateOnly.FromDateTime(DateTime.Now.Date)
            : DateOnly.FromDateTime(args.NavigationDate.Date);

        var displayRequest = new CalendarDisplayRequest(statePersistanceService.CalendarDisplayType, targetDate);

        WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(displayRequest, args.ForceReload, args.PendingTarget));
    }
}
