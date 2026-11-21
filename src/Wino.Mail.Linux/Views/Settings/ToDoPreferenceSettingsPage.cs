using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>To Do preferences (Windows ToDoPreferenceSettingsPage): new tasks, start view, completed tasks, sound and delete confirmation.</summary>
internal sealed class ToDoPreferenceSettingsPage(ToDoPreferenceSettingsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ToDoPreferenceSettingsPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var newTasks = WinoSettingsRows.Action(Translator.ToDoSettings_NewTasks_Header, Translator.ToDoSettings_NewTasks_Description, WinoIconGlyph.Add);
        WinoSettingsRows.Suffix(newTasks, Settings.ChoiceSuffix<ToDoPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, Translator.ToDoSettings_NewTasks_Header,
            static s => s.DestinationBehaviors, static o => o.DisplayText,
            nameof(vm.SelectedDestinationBehavior), static s => s.SelectedDestinationBehavior, static (s, v) => s.SelectedDestinationBehavior = v));
        var creationList = Settings.Visible(Dependent(TaskList(vm, Translator.ToDoSettings_NewTasks_Header,
            nameof(vm.SelectedCreationList), static s => s.SelectedCreationList, static (s, v) => s.SelectedCreationList = v)),
            vm, nameof(vm.ShouldShowSpecificCreationList), static s => s.ShouldShowSpecificCreationList);

        var startView = WinoSettingsRows.Action(Translator.ToDoSettings_StartView_Header, Translator.ToDoSettings_StartView_Description, WinoIconGlyph.Home);
        WinoSettingsRows.Suffix(startView, Settings.ChoiceSuffix<ToDoPreferenceSettingsPageViewModel, ToDoStartViewOption>(vm, Translator.ToDoSettings_StartView_Header,
            static s => s.StartViews, static o => o.DisplayText,
            nameof(vm.SelectedStartView), static s => s.SelectedStartView, static (s, v) => s.SelectedStartView = v));
        var startList = Settings.Visible(Dependent(TaskList(vm, Translator.ToDoSettings_StartView_Header,
            nameof(vm.SelectedStartList), static s => s.SelectedStartList, static (s, v) => s.SelectedStartList = v)),
            vm, nameof(vm.ShouldShowSpecificStartList), static s => s.ShouldShowSpecificStartList);

        var completed = WinoSettingsRows.Action(Translator.ToDoSettings_Completed_Header, Translator.ToDoSettings_Completed_Description, WinoIconGlyph.Checkmark);
        WinoSettingsRows.Suffix(completed, Settings.ChoiceSuffix<ToDoPreferenceSettingsPageViewModel, CompletedTaskTreatmentOption>(vm, Translator.ToDoSettings_Completed_Header,
            static s => s.CompletedTaskTreatments, static o => o.DisplayText,
            nameof(vm.SelectedCompletedTaskTreatment), static s => s.SelectedCompletedTaskTreatment, static (s, v) => s.SelectedCompletedTaskTreatment = v));
        var hideDelay = Settings.Visible(Dependent(Settings.ChoiceSuffix<ToDoPreferenceSettingsPageViewModel, CompletedTaskHideDelayOption>(vm, Translator.ToDoSettings_Completed_Header,
            static s => s.CompletedTaskHideDelays, static o => o.DisplayText,
            nameof(vm.SelectedCompletedTaskHideDelay), static s => s.SelectedCompletedTaskHideDelay, static (s, v) => s.SelectedCompletedTaskHideDelay = v)),
            vm, nameof(vm.ShouldShowHideDelay), static s => s.ShouldShowHideDelay);

        AddGroup(null,
            newTasks,
            creationList,
            startView,
            startList,
            completed,
            hideDelay,
            // Stored now; audible once PL-06 gives Linux its completion sound.
            Settings.Switch(vm, Translator.ToDoSettings_Sound_Header, Translator.ToDoSettings_Sound_Description, WinoIconGlyph.Speaker,
                nameof(vm.IsTaskCompletionSoundEnabled), static s => s.IsTaskCompletionSoundEnabled, static (s, v) => s.IsTaskCompletionSoundEnabled = v),
            Settings.Switch(vm, Translator.ToDoSettings_DeleteConfirmation_Header, Translator.ToDoSettings_DeleteConfirmation_Description, WinoIconGlyph.Delete,
                nameof(vm.IsTaskDeleteConfirmationEnabled), static s => s.IsTaskDeleteConfirmationEnabled, static (s, v) => s.IsTaskDeleteConfirmationEnabled = v));
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);

    // Two drop-downs beside the text squeeze the title to a letter per line, so the dependent one gets the row below.
    private static Adw.PreferencesRow Dependent(Gtk.DropDown dropDown)
    {
        dropDown.SetHalign(Gtk.Align.End);
        dropDown.SetHexpand(true);
        return WinoSettingsRows.Custom(dropDown);
    }

    private Gtk.DropDown TaskList(ToDoPreferenceSettingsPageViewModel vm, string accessibleName, string property,
        Func<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption?> read, Action<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption> write)
        => Settings.ChoiceSuffix(vm, accessibleName, static s => s.TaskLists, static o => o.DisplayText, property, read, write, itemsCollection: vm.TaskLists);
}
