#if DEBUG
using Wino.Core.Domain.Enums;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>Debug-bridge hooks (Mac twins with the same names and output), driven by the calendar page's debug commands.</summary>
public sealed partial class EventDetailsPageViewController
{
    /// <summary>Picks a Show as value through the menu path and waits for the save.</summary>
    public async Task<string> DebugChooseShowAsAsync(string value)
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!Enum.TryParse<CalendarItemShowAs>(value, true, out var showAs)) return;
            var option = ViewModel.ShowAsOptions.FirstOrDefault(candidate => candidate.ShowAs == showAs);
            if (option is null) return;
            ShowAsChosen(option);
        });
        return await DebugAfterSaveAsync();
    }

    /// <summary>Toggles the reminder with <paramref name="minutes"/> through the menu path and waits for the save.</summary>
    public async Task<string> DebugToggleReminderAsync(int minutes)
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var option = ViewModel.ReminderOptions.FirstOrDefault(candidate => candidate.Minutes == minutes);
            if (option is not null) ToggleReminder(option);
        });
        return await DebugAfterSaveAsync();
    }

    private async Task<string> DebugAfterSaveAsync()
    {
        for (int attempt = 0; attempt < 50 && (_saving || _savePending); attempt++) await Task.Delay(100);
        string result = string.Empty;
        // iOS has no pop-out event window, so inWindow is always False (kept for the Mac output format).
        await Dispatcher.ExecuteOnUIThread(() => result =
            $"saved={ViewModel.LastMutationSucceeded} showAs={ViewModel.CurrentEvent?.CalendarItem.ShowAs} popup={_showAs.Configuration?.Title} " +
            $"reminders=[{string.Join(",", (ViewModel.Reminders ?? []).Select(reminder => reminder.DurationInSeconds / 60))}] row='{_reminderRow.Text}' inWindow={false}");
        return result;
    }

    /// <summary>Runs Save on the attachment at <paramref name="index"/> (the folder picker needs a manual choice).</summary>
    public string DebugSaveAttachment(int index)
    {
        if (index < 0 || index >= ViewModel.Attachments.Count) return $"only {ViewModel.Attachments.Count} attachments";
        SaveAttachment(ViewModel.Attachments[index]);
        return "save started for " + ViewModel.Attachments[index].FileName;
    }
}
#endif
