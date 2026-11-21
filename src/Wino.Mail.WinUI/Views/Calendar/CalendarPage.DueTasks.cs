using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Views.Calendar;

public sealed partial class CalendarPage
{
    private async void DueTaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: TaskItemViewModel task } checkBox || ViewModel.DueTasks is not { } dueTasks)
            return;

        // The row leaves the strip once the task is completed; until then the box shows the stored state.
        checkBox.IsChecked = task.IsCompleted;
        await dueTasks.CompleteCommand.ExecuteAsync(task);
    }
}
