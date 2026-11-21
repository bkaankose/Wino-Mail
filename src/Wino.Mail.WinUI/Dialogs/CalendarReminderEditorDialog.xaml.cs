using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>Edits any number of reminders of an event, each before or after its start.</summary>
public sealed partial class CalendarReminderEditorDialog : ContentDialog
{
    public CalendarReminderEditorViewModel Editor { get; }

    internal List<CalendarReminderDraft>? Result { get; private set; }

    public CalendarReminderEditorDialog(List<CalendarReminderDraft> reminders, bool supportsEmailReminder)
    {
        Editor = new CalendarReminderEditorViewModel(reminders, supportsEmailReminder);
        InitializeComponent();
    }

    private void RemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CalendarReminderEditorRow row })
            Editor.RemoveReminderCommand.Execute(row);
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = Editor.GetReminders();
}
