using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Services.Companion;

namespace Wino.Mail.WinUI.Controls.Companion;

public sealed partial class CompanionFlyoutView : UserControl
{
    internal CompanionFlyoutView(CompanionDashboardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public CompanionDashboardViewModel ViewModel { get; }

    internal event EventHandler? HideRequested;

    private async void OpenEvent_Click(object? sender, EventArgs e) => await OpenEventAsync();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;

        e.Handled = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void SnoozeSplitButton_IsCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        // The event also fires when the binding pushes state in, so only act on a real divergence.
        // Without this the preference change would loop straight back into another write.
        if (sender.IsChecked == ViewModel.SnoozeNotifications)
            return;

        try
        {
            if (sender.IsChecked)
            {
                // Pressing the button itself is the open-ended choice: hold everything until the
                // user turns it back on. Timed durations come from the dropdown.
                await ViewModel.StartNotificationSnoozeCommand.ExecuteAsync(NotificationSnoozePreset.UntilTurnedBackOn);
            }
            else
            {
                await ViewModel.ResumeNotificationsCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private async void SnoozePreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<NotificationSnoozePreset>(tag, out var preset))
            {
                await ViewModel.StartNotificationSnoozeCommand.ExecuteAsync(preset);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAsync(System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void LaterEvent_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.OpenCalendarEventCommand.ExecuteAsync((CalendarItemViewModel)parameter));

    private async Task OpenEventAsync()
    {
        try
        {
            if (ViewModel.NextEvent is { } calendarItem)
                await ViewModel.OpenCalendarEventCommand.ExecuteAsync(calendarItem);
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private async void Mail_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.OpenMailCommand.ExecuteAsync((MailItemViewModel)parameter));

    private async void MailRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return;

            if (sender is FrameworkElement { Tag: MailItemViewModel mail })
                await ViewModel.OpenMailCommand.ExecuteAsync(mail);
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private void MailRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetHoverActionsVisibility(sender, Visibility.Visible);

    private void MailRow_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetHoverActionsVisibility(sender, Visibility.Collapsed);

    private async void ArchiveMail_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.ArchiveMailCommand.ExecuteAsync((MailItemViewModel)parameter));

    private async void MarkMailRead_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.MarkMailReadCommand.ExecuteAsync((MailItemViewModel)parameter));

    private async void TaskCompleted_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.SetTaskCompletedCommand.ExecuteAsync((TaskItemViewModel)parameter));

    private async void Contact_Click(object sender, RoutedEventArgs e)
        => await ExecuteCommandAsync(
            sender,
            static (viewModel, parameter) => viewModel.FindContactCommand.ExecuteAsync((AccountContactViewModel)parameter));

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

    private static void SetHoverActionsVisibility(object sender, Visibility visibility)
    {
        if (sender is Panel panel && panel.Children.LastOrDefault() is FrameworkElement hoverActions)
            hoverActions.Visibility = visibility;
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase)
                return true;

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
