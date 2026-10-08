using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wino.Mail.ViewModels.Companion;

namespace Wino.Mail.WinUI.Services.Companion;

/// <summary>
/// The WinUI flyout's view of the shared <see cref="Wino.Mail.ViewModels.Companion.CompanionDashboardViewModel"/>:
/// adds the <see cref="Visibility"/> properties the XAML binds to, computed from the shared booleans.
/// </summary>
public sealed partial class CompanionDashboardViewModel : Wino.Mail.ViewModels.Companion.CompanionDashboardViewModel
{
    // Shared boolean -> the WinUI visibility property derived from it.
    private static readonly Dictionary<string, string> VisibilityProperties = new()
    {
        [nameof(IsInitializing)] = nameof(InitializingVisibility),
        [nameof(IsNoAccounts)] = nameof(NoAccountsVisibility),
        [nameof(IsReady)] = nameof(ReadyVisibility),
        [nameof(IsUnavailable)] = nameof(UnavailableVisibility),
        [nameof(HasEvent)] = nameof(EventVisibility),
        [nameof(HasLaterEvents)] = nameof(LaterEventsVisibility),
        [nameof(HasUnreadMail)] = nameof(UnreadMailVisibility),
        [nameof(HasTasks)] = nameof(TasksVisibility),
        [nameof(HasFavorites)] = nameof(FavoritesVisibility),
        [nameof(IsAllCaughtUp)] = nameof(CaughtUpVisibility),
        [nameof(HasContent)] = nameof(ContentVisibility),
        [nameof(ShowEventMailSeparator)] = nameof(EventMailSeparatorVisibility),
        [nameof(ShowMailTaskSeparator)] = nameof(MailTaskSeparatorVisibility),
        [nameof(SnoozeNotifications)] = nameof(SnoozeInfoVisibility),
        [nameof(IsCustomSnoozeVisible)] = nameof(CustomSnoozeVisibility),
        [nameof(HasNextEventLocation)] = nameof(NextEventLocationVisibility),
        [nameof(IsNextEventOnline)] = nameof(NextEventOnlineVisibility),
        [nameof(HasNextEventCalendar)] = nameof(NextEventCalendarVisibility),
        [nameof(HasNextEventAttendees)] = nameof(NextEventAttendeesVisibility),
    };

    internal CompanionDashboardViewModel(
        System.IServiceProvider services,
        ICompanionActionHandler actions,
        DispatcherQueue dispatcher,
        System.DateTimeOffset sessionStartedAtUtc)
        : base(services, actions, new WinUIDispatcher(dispatcher), sessionStartedAtUtc)
    {
    }

    public Visibility InitializingVisibility => ToVisibility(IsInitializing);
    public Visibility NoAccountsVisibility => ToVisibility(IsNoAccounts);
    public Visibility ReadyVisibility => ToVisibility(IsReady);
    public Visibility UnavailableVisibility => ToVisibility(IsUnavailable);
    public Visibility EventVisibility => ToVisibility(HasEvent);
    public Visibility LaterEventsVisibility => ToVisibility(HasLaterEvents);
    public Visibility UnreadMailVisibility => ToVisibility(HasUnreadMail);
    public Visibility TasksVisibility => ToVisibility(HasTasks);
    public Visibility FavoritesVisibility => ToVisibility(HasFavorites);
    public Visibility CaughtUpVisibility => ToVisibility(IsAllCaughtUp);
    public Visibility ContentVisibility => ToVisibility(HasContent);
    public Visibility EventMailSeparatorVisibility => ToVisibility(ShowEventMailSeparator);
    public Visibility MailTaskSeparatorVisibility => ToVisibility(ShowMailTaskSeparator);
    public Visibility SnoozeInfoVisibility => ToVisibility(SnoozeNotifications);
    public Visibility CustomSnoozeVisibility => ToVisibility(IsCustomSnoozeVisible);
    public Visibility NextEventLocationVisibility => ToVisibility(HasNextEventLocation);
    public Visibility NextEventOnlineVisibility => ToVisibility(IsNextEventOnline);
    public Visibility NextEventCalendarVisibility => ToVisibility(HasNextEventCalendar);
    public Visibility NextEventAttendeesVisibility => ToVisibility(HasNextEventAttendees);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is { } name && VisibilityProperties.TryGetValue(name, out var visibilityName))
            base.OnPropertyChanged(new PropertyChangedEventArgs(visibilityName));
    }

    private static Visibility ToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;
}
