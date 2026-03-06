using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Dialogs;

public sealed partial class SingleCalendarPickerDialog : ContentDialog
{
    public AccountCalendar? PickedCalendar { get; private set; }

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
}
