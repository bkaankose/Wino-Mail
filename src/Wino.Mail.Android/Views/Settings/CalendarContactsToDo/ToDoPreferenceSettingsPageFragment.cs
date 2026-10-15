using Android.Widget;
using Android.Views;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Android.Views.Settings.CalendarContactsToDo;

/// <summary>
/// To Do preferences: new task list, start view, completed tasks, sound and delete confirmation (Windows
/// ToDoPreferenceSettingsPage, iOS ToDoPreferenceSettingsPageViewController). Each pop-up pair stacks under the card
/// text; the second pop-up shows only when the first choice asks for it.
/// </summary>
public sealed class ToDoPreferenceSettingsPageFragment : SettingsPageFragment<ToDoPreferenceSettingsPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var creationList = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption>(vm, s => s.TaskLists.ToList(), o => o.DisplayText,
            nameof(vm.SelectedCreationList), s => s.SelectedCreationList, (s, v) => s.SelectedCreationList = v, itemsCollection: vm.TaskLists);
        Bind.Visible(creationList, vm, nameof(vm.ShouldShowSpecificCreationList), s => s.ShouldShowSpecificCreationList);
        var startList = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, TaskListPreferenceOption>(vm, s => s.TaskLists.ToList(), o => o.DisplayText,
            nameof(vm.SelectedStartList), s => s.SelectedStartList, (s, v) => s.SelectedStartList = v, itemsCollection: vm.TaskLists);
        Bind.Visible(startList, vm, nameof(vm.ShouldShowSpecificStartList), s => s.ShouldShowSpecificStartList);
        var hideDelay = Bind.PopUp<ToDoPreferenceSettingsPageViewModel, CompletedTaskHideDelayOption>(vm, s => s.CompletedTaskHideDelays.ToList(), o => o.DisplayText,
            nameof(vm.SelectedCompletedTaskHideDelay), s => s.SelectedCompletedTaskHideDelay, (s, v) => s.SelectedCompletedTaskHideDelay = v);
        Bind.Visible(hideDelay, vm, nameof(vm.ShouldShowHideDelay), s => s.ShouldShowHideDelay);

        AddGroup(null,
            StackedCard(Translator.ToDoSettings_NewTasks_Header, Translator.ToDoSettings_NewTasks_Description, WinoIconGlyph.Add,
                Pair(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, s => s.DestinationBehaviors.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedDestinationBehavior), s => s.SelectedDestinationBehavior, (s, v) => s.SelectedDestinationBehavior = v), creationList)),
            StackedCard(Translator.ToDoSettings_StartView_Header, Translator.ToDoSettings_StartView_Description, WinoIconGlyph.Home,
                Pair(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, ToDoStartViewOption>(vm, s => s.StartViews.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedStartView), s => s.SelectedStartView, (s, v) => s.SelectedStartView = v), startList)),
            StackedCard(Translator.ToDoSettings_Completed_Header, Translator.ToDoSettings_Completed_Description, WinoIconGlyph.Checkmark,
                Pair(Bind.PopUp<ToDoPreferenceSettingsPageViewModel, CompletedTaskTreatmentOption>(vm, s => s.CompletedTaskTreatments.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedCompletedTaskTreatment), s => s.SelectedCompletedTaskTreatment, (s, v) => s.SelectedCompletedTaskTreatment = v), hideDelay)),
            Card(Translator.ToDoSettings_Sound_Header, Translator.ToDoSettings_Sound_Description, WinoIconGlyph.Speaker,
                Bind.Switch(vm, nameof(vm.IsTaskCompletionSoundEnabled), s => s.IsTaskCompletionSoundEnabled, (s, v) => s.IsTaskCompletionSoundEnabled = v, Translator.ToDoSettings_Sound_Header)),
            Card(Translator.ToDoSettings_DeleteConfirmation_Header, Translator.ToDoSettings_DeleteConfirmation_Description, WinoIconGlyph.Delete,
                Bind.Switch(vm, nameof(vm.IsTaskDeleteConfirmationEnabled), s => s.IsTaskDeleteConfirmationEnabled, (s, v) => s.IsTaskDeleteConfirmationEnabled = v, Translator.ToDoSettings_DeleteConfirmation_Header)));
    }

    /// <summary>The choice pop-up with its dependent pop-up under it (side by side on the Mac).</summary>
    private LinearLayout Pair(View first, View second) => Stack(Resource.Dimension.wino_space_s, first, second);
}
