using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using MailCal.Mail.WinUI.Services.Companion;

namespace MailCal.Mail.WinUI.Controls.Companion;

public sealed partial class CompanionFlyoutView : UserControl
{
    // Set while the view pushes the view model's date into the CalendarView, so that change is not
    // read back as a user selection.
    private bool _isSyncingCalendar;

    internal CompanionFlyoutView(CompanionDashboardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) => SyncCalendarSelection();
    }

    public CompanionDashboardViewModel ViewModel { get; }

    internal event EventHandler? HideRequested;

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;

        e.Handled = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedDate))
            SyncCalendarSelection();
    }

    private void MonthCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_isSyncingCalendar)
            return;

        if (args.AddedDates.Count > 0)
        {
            ViewModel.SelectedDate = args.AddedDates[0].Date;
            return;
        }

        // Clicking the selected day clears the selection; a day is always selected here.
        if (sender.SelectedDates.Count == 0)
            SyncCalendarSelection();
    }

    private void SyncCalendarSelection()
    {
        var selected = new DateTimeOffset(ViewModel.SelectedDate);
        if (MonthCalendar.SelectedDates.Count == 1 && MonthCalendar.SelectedDates[0].Date == ViewModel.SelectedDate)
            return;

        _isSyncingCalendar = true;
        try
        {
            MonthCalendar.SelectedDates.Clear();
            MonthCalendar.SelectedDates.Add(selected);
            MonthCalendar.SetDisplayDate(selected);
        }
        finally
        {
            _isSyncingCalendar = false;
        }
    }

    private void QuickTitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        if (ViewModel.CreateQuickEventCommand.CanExecute(null))
            ViewModel.CreateQuickEventCommand.Execute(null);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAsync(System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private async void DayEvent_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.OpenCalendarEventCommand.ExecuteAsync((MailCal.Mail.ViewModels.Companion.CompanionDayEvent)parameter));

    private async Task ExecuteCommandAsync(
        object sender,
        Func<CompanionDashboardViewModel, object, Task> execute)
    {
        try
        {
            if (sender is ButtonBase { CommandParameter: { } parameter })
                await execute(ViewModel, parameter);
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }
}
