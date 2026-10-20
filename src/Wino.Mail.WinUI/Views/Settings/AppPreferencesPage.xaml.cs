using Microsoft.UI.Xaml;
using Wino.Core.Domain.Enums;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class AppPreferencesPage : AppPreferencesPageAbstract
{
    public AppPreferencesPage()
    {
        this.InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void StartMenuEntryToggled(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded) return;
        if (ReferenceEquals(sender, CalendarStartMenuToggle))
            RequestStartMenuChange(WinoApplicationMode.Calendar, CalendarStartMenuToggle.IsOn, ViewModel.IsCalendarStartMenuEntryEnabled);
        else if (ReferenceEquals(sender, ContactsStartMenuToggle))
            RequestStartMenuChange(WinoApplicationMode.Contacts, ContactsStartMenuToggle.IsOn, ViewModel.IsContactsStartMenuEntryEnabled);
        else if (ReferenceEquals(sender, TasksStartMenuToggle))
            RequestStartMenuChange(WinoApplicationMode.Tasks, TasksStartMenuToggle.IsOn, ViewModel.IsTasksStartMenuEntryEnabled);
    }

    private void RequestStartMenuChange(WinoApplicationMode mode, bool requested, bool current)
    {
        if (requested != current && ViewModel.ToggleStartMenuEntryCommand.CanExecute(mode))
            ViewModel.ToggleStartMenuEntryCommand.Execute(mode);
    }
}
