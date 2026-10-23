using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MailCal.Mail.ViewModels.Companion;

namespace MailCal.Mail.WinUI.Services.Companion;

/// <summary>
/// The WinUI flyout's view of the shared <see cref="MailCal.Mail.ViewModels.Companion.CompanionDashboardViewModel"/>:
/// adds the <see cref="Visibility"/> properties the XAML binds to, computed from the shared booleans.
/// </summary>
public sealed partial class CompanionDashboardViewModel : MailCal.Mail.ViewModels.Companion.CompanionDashboardViewModel
{
    // Shared boolean -> the WinUI visibility property derived from it.
    private static readonly Dictionary<string, string[]> VisibilityProperties = new()
    {
        [nameof(IsInitializing)] = [nameof(InitializingVisibility)],
        [nameof(IsNoAccounts)] = [nameof(NoAccountsVisibility)],
        [nameof(IsReady)] = [nameof(ReadyVisibility)],
        [nameof(IsUnavailable)] = [nameof(UnavailableVisibility)],
        [nameof(HasDayEvents)] = [nameof(DayEventsVisibility), nameof(DayEmptyVisibility)],
        [nameof(HasWritableCalendars)] = [nameof(QuickAddVisibility), nameof(NoWritableCalendarsVisibility)],
        [nameof(QuickIsAllDay)] = [nameof(QuickTimesVisibility)],
        [nameof(IsTodaySelected)] = [nameof(TodayButtonVisibility)],
        [nameof(HasError)] = [nameof(ErrorVisibility)],
    };

    internal CompanionDashboardViewModel(
        System.IServiceProvider services,
        ICompanionActionHandler actions,
        DispatcherQueue dispatcher)
        : base(services, actions, new WinUIDispatcher(dispatcher))
    {
    }

    public Visibility InitializingVisibility => ToVisibility(IsInitializing);
    public Visibility NoAccountsVisibility => ToVisibility(IsNoAccounts);
    public Visibility ReadyVisibility => ToVisibility(IsReady);
    public Visibility UnavailableVisibility => ToVisibility(IsUnavailable);
    public Visibility DayEventsVisibility => ToVisibility(HasDayEvents);
    public Visibility DayEmptyVisibility => ToVisibility(!HasDayEvents);
    public Visibility QuickAddVisibility => ToVisibility(HasWritableCalendars);
    public Visibility NoWritableCalendarsVisibility => ToVisibility(!HasWritableCalendars);
    public Visibility QuickTimesVisibility => ToVisibility(!QuickIsAllDay);
    public Visibility TodayButtonVisibility => ToVisibility(!IsTodaySelected);
    public Visibility ErrorVisibility => ToVisibility(HasError);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is { } name && VisibilityProperties.TryGetValue(name, out var visibilityNames))
        {
            foreach (var visibilityName in visibilityNames)
                base.OnPropertyChanged(new PropertyChangedEventArgs(visibilityName));
        }
    }

    private static Visibility ToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;
}
