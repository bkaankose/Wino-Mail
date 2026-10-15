using Google.Android.Material.DatePicker;
using Wino.Core.Domain;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Wino.Mail.Android.Views.ToDo;

/// <summary>
/// "Pick a date" for a task's due date (Windows TaskDueDatePickerHost, the iOS inline date sheet): the Material 3 date
/// picker dialog. The picker works in UTC midnight milliseconds; the chosen calendar day is handed back as a local date.
/// </summary>
internal static class TaskDatePicker
{
    public static void Show(Fragment owner, DateTime? current, Action<DateTime> picked)
    {
        if (!owner.IsAdded || owner.ChildFragmentManager.IsStateSaved) return;
        var day = (current ?? DateTime.Now).Date;
        long selection = (long)(new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;
        var picker = MaterialDatePicker.Builder.DatePicker()
            .SetTitleText(Translator.ToDoPage_DuePresetPickDate)
            .SetSelection(Java.Lang.Long.ValueOf(selection))
            .Build();
        picker.AddOnPositiveButtonClickListener(new Positive(value =>
        {
            if (value is not Java.Lang.Long milliseconds) return;
            var utc = DateTime.UnixEpoch.AddMilliseconds(milliseconds.LongValue());
            picked(new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Local));
        }));
        picker.Show(owner.ChildFragmentManager, "wino.task.date");
    }

    private sealed class Positive(Action<Java.Lang.Object?> run) : Java.Lang.Object, IMaterialPickerOnPositiveButtonClickListener
    {
        public void OnPositiveButtonClick(Java.Lang.Object? selection) => run(selection);
    }
}
