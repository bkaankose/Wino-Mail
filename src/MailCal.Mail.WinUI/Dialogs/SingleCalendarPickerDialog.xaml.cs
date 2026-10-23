using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Dialogs;

public sealed partial class SingleCalendarPickerDialog : ContentDialog
{
    public AccountCalendar? PickedCalendar { get; private set; }
    public bool ShouldNavigateToCalendarSettings { get; private set; }

    public List<CalendarPickerAccountGroup> AvailableGroups { get; } = [];

    public SingleCalendarPickerDialog(List<CalendarPickerAccountGroup> availableGroups)
    {
        AvailableGroups = availableGroups;

        InitializeComponent();
    }

    private void CalendarClicked(object sender, ItemClickEventArgs e)
    {
        PickedCalendar = e.ClickedItem as AccountCalendar;
        Hide();
    }

    private void OpenCalendarSettingsClicked(object sender, RoutedEventArgs e)
    {
        ShouldNavigateToCalendarSettings = true;
        Hide();
    }
}
