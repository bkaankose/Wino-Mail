using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>To Do preferences: new task list, start view, completed tasks, sound and delete confirmation (Windows ToDoPreferenceSettingsPage).</summary>
public sealed class ToDoPreferenceSettingsPageViewController(ToDoPreferenceSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ToDoPreferenceSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var creationList = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption>(vm, s => s.TaskLists.ToList(), o => o.DisplayText,
            nameof(vm.SelectedCreationList), s => s.SelectedCreationList, (s, v) => s.SelectedCreationList = v, width: 180, itemsCollection: vm.TaskLists);
        Bind.Visible(creationList, vm, nameof(vm.ShouldShowSpecificCreationList), s => s.ShouldShowSpecificCreationList);
        var startList = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption>(vm, s => s.TaskLists.ToList(), o => o.DisplayText,
            nameof(vm.SelectedStartList), s => s.SelectedStartList, (s, v) => s.SelectedStartList = v, width: 180, itemsCollection: vm.TaskLists);
        Bind.Visible(startList, vm, nameof(vm.ShouldShowSpecificStartList), s => s.ShouldShowSpecificStartList);
        var hideDelay = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, CompletedTaskHideDelayOption>(vm, s => s.CompletedTaskHideDelays.ToList(), o => o.DisplayText,
            nameof(vm.SelectedCompletedTaskHideDelay), s => s.SelectedCompletedTaskHideDelay, (s, v) => s.SelectedCompletedTaskHideDelay = v, width: 140);
        Bind.Visible(hideDelay, vm, nameof(vm.ShouldShowHideDelay), s => s.ShouldShowHideDelay);

        AddGroup(null,
            Card(Translator.ToDoSettings_NewTasks_Header, Translator.ToDoSettings_NewTasks_Description, WinoIconGlyph.Add,
                Row(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, s => s.DestinationBehaviors.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedDestinationBehavior), s => s.SelectedDestinationBehavior, (s, v) => s.SelectedDestinationBehavior = v, width: 160), creationList)),
            Card(Translator.ToDoSettings_StartView_Header, Translator.ToDoSettings_StartView_Description, WinoIconGlyph.Home,
                Row(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, ToDoStartViewOption>(vm, s => s.StartViews.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedStartView), s => s.SelectedStartView, (s, v) => s.SelectedStartView = v, width: 160), startList)),
            Card(Translator.ToDoSettings_Completed_Header, Translator.ToDoSettings_Completed_Description, WinoIconGlyph.Checkmark,
                Row(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, CompletedTaskTreatmentOption>(vm, s => s.CompletedTaskTreatments.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedCompletedTaskTreatment), s => s.SelectedCompletedTaskTreatment, (s, v) => s.SelectedCompletedTaskTreatment = v, width: 160), hideDelay)),
            Card(Translator.ToDoSettings_Sound_Header, Translator.ToDoSettings_Sound_Description, WinoIconGlyph.Speaker,
                Bind.Switch(vm, nameof(vm.IsTaskCompletionSoundEnabled), s => s.IsTaskCompletionSoundEnabled, (s, v) => s.IsTaskCompletionSoundEnabled = v, Translator.ToDoSettings_Sound_Header)),
            Card(Translator.ToDoSettings_DeleteConfirmation_Header, Translator.ToDoSettings_DeleteConfirmation_Description, WinoIconGlyph.Delete,
                Bind.Switch(vm, nameof(vm.IsTaskDeleteConfirmationEnabled), s => s.IsTaskDeleteConfirmationEnabled, (s, v) => s.IsTaskDeleteConfirmationEnabled = v, Translator.ToDoSettings_DeleteConfirmation_Header)));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);
}
