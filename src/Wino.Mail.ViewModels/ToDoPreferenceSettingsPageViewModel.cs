using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class ToDoPreferenceSettingsPageViewModel(
    IPreferencesService preferencesService,
    ITaskQueryService taskService,
    IAccountService accountService) : CoreBaseViewModel
{
    private bool _isLoaded;

    public ObservableCollection<DestinationBehaviorOption> DestinationBehaviors { get; } =
    [
        new(NewItemDestinationBehavior.AskEachTime, Translator.ProductivitySettings_AskEveryTime),
        new(NewItemDestinationBehavior.LastUsed, Translator.ProductivitySettings_LastUsed),
        new(NewItemDestinationBehavior.Specific, Translator.ProductivitySettings_SpecificDestination)
    ];

    public ObservableCollection<ToDoStartViewOption> StartViews { get; } =
    [
        new(ToDoStartView.MyDay, Translator.ToDoPage_MyDay),
        new(ToDoStartView.Planned, Translator.ToDoPage_Planned),
        new(ToDoStartView.AllTasks, Translator.ToDoSettings_StartView_AllTasks),
        new(ToDoStartView.SpecificList, Translator.ToDoSettings_StartView_SpecificList)
    ];

    public ObservableCollection<CompletedTaskTreatmentOption> CompletedTaskTreatments { get; } =
    [
        new(CompletedTaskTreatment.StayVisible, Translator.ToDoSettings_Completed_StayVisible),
        new(CompletedTaskTreatment.MoveToBottom, Translator.ToDoSettings_Completed_MoveToBottom),
        new(CompletedTaskTreatment.HideAfterPeriod, Translator.ToDoSettings_Completed_HideAfter)
    ];

    public ObservableCollection<CompletedTaskHideDelayOption> CompletedTaskHideDelays { get; } =
    [
        new(CompletedTaskHideDelay.OneDay, Translator.ToDoSettings_HideDelay_OneDay),
        new(CompletedTaskHideDelay.SevenDays, Translator.ToDoSettings_HideDelay_SevenDays),
        new(CompletedTaskHideDelay.ThirtyDays, Translator.ToDoSettings_HideDelay_ThirtyDays)
    ];

    public ObservableCollection<TaskListPreferenceOption> TaskLists { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowSpecificCreationList))]
    public partial DestinationBehaviorOption SelectedDestinationBehavior { get; set; }

    [ObservableProperty]
    public partial TaskListPreferenceOption SelectedCreationList { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowSpecificStartList))]
    public partial ToDoStartViewOption SelectedStartView { get; set; }

    [ObservableProperty]
    public partial TaskListPreferenceOption SelectedStartList { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowHideDelay))]
    public partial CompletedTaskTreatmentOption SelectedCompletedTaskTreatment { get; set; }

    [ObservableProperty]
    public partial CompletedTaskHideDelayOption SelectedCompletedTaskHideDelay { get; set; }

    [ObservableProperty]
    public partial bool IsTaskCompletionSoundEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsTaskDeleteConfirmationEnabled { get; set; }

    public bool ShouldShowSpecificCreationList
        => SelectedDestinationBehavior?.Behavior == NewItemDestinationBehavior.Specific;

    public bool ShouldShowSpecificStartList
        => SelectedStartView?.View == ToDoStartView.SpecificList;

    public bool ShouldShowHideDelay
        => SelectedCompletedTaskTreatment?.Treatment == CompletedTaskTreatment.HideAfterPeriod;

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isLoaded = false;

        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountNames = accounts.ToDictionary(account => account.Id, account => account.Name);
        var taskLists = (await taskService.GetTaskListsAsync().ConfigureAwait(false))
            .Where(list => !list.IsReadOnly)
            .Select(list => new TaskListPreferenceOption(
                list,
                accountNames.GetValueOrDefault(list.MailAccountId) ?? Translator.ToDoPage_Tasks))
            .OrderBy(item => item.AccountName)
            .ThenBy(item => item.List.Title)
            .ToList();

        await ExecuteUIThread(() => ApplyLoadedState(taskLists));
    }

    private void ApplyLoadedState(IReadOnlyList<TaskListPreferenceOption> taskLists)
    {
        TaskLists.Clear();
        foreach (var list in taskLists)
            TaskLists.Add(list);

        var creationList = ResolveList(preferencesService.SpecificTaskListId);
        if (preferencesService.TaskCreationBehavior == NewItemDestinationBehavior.Specific && creationList is null)
        {
            preferencesService.TaskCreationBehavior = NewItemDestinationBehavior.AskEachTime;
            preferencesService.SpecificTaskListId = null;
        }

        var startList = ResolveList(preferencesService.ToDoStartTaskListId);
        if (preferencesService.ToDoStartView == ToDoStartView.SpecificList && startList is null)
        {
            preferencesService.ToDoStartView = ToDoStartView.MyDay;
            preferencesService.ToDoStartTaskListId = null;
        }

        SelectedDestinationBehavior = DestinationBehaviors.First(option => option.Behavior == preferencesService.TaskCreationBehavior);
        SelectedCreationList = creationList ?? TaskLists.FirstOrDefault();
        SelectedStartView = StartViews.First(option => option.View == preferencesService.ToDoStartView);
        SelectedStartList = startList ?? TaskLists.FirstOrDefault();
        SelectedCompletedTaskTreatment = CompletedTaskTreatments.First(option => option.Treatment == preferencesService.CompletedTaskTreatment);
        SelectedCompletedTaskHideDelay = CompletedTaskHideDelays.First(option => option.Delay == preferencesService.CompletedTaskHideDelay);
        IsTaskCompletionSoundEnabled = preferencesService.IsTaskCompletionSoundEnabled;
        IsTaskDeleteConfirmationEnabled = preferencesService.IsTaskDeleteConfirmationEnabled;
        _isLoaded = true;
    }

    private TaskListPreferenceOption ResolveList(System.Guid? listId)
        => listId.HasValue ? TaskLists.FirstOrDefault(item => item.List.Id == listId.Value) : null;

    partial void OnSelectedDestinationBehaviorChanged(DestinationBehaviorOption value)
    {
        if (!_isLoaded || value is null)
            return;

        preferencesService.TaskCreationBehavior = value.Behavior;
        preferencesService.SpecificTaskListId = value.Behavior == NewItemDestinationBehavior.Specific
            ? SelectedCreationList?.List.Id
            : null;
    }

    partial void OnSelectedCreationListChanged(TaskListPreferenceOption value)
    {
        if (_isLoaded && SelectedDestinationBehavior?.Behavior == NewItemDestinationBehavior.Specific)
            preferencesService.SpecificTaskListId = value?.List.Id;
    }

    partial void OnSelectedStartViewChanged(ToDoStartViewOption value)
    {
        if (!_isLoaded || value is null)
            return;

        preferencesService.ToDoStartView = value.View;
        preferencesService.ToDoStartTaskListId = value.View == ToDoStartView.SpecificList
            ? SelectedStartList?.List.Id
            : null;
    }

    partial void OnSelectedStartListChanged(TaskListPreferenceOption value)
    {
        if (_isLoaded && SelectedStartView?.View == ToDoStartView.SpecificList)
            preferencesService.ToDoStartTaskListId = value?.List.Id;
    }

    partial void OnSelectedCompletedTaskTreatmentChanged(CompletedTaskTreatmentOption value)
    {
        if (_isLoaded && value is not null)
            preferencesService.CompletedTaskTreatment = value.Treatment;
    }

    partial void OnSelectedCompletedTaskHideDelayChanged(CompletedTaskHideDelayOption value)
    {
        if (_isLoaded && value is not null)
            preferencesService.CompletedTaskHideDelay = value.Delay;
    }

    partial void OnIsTaskCompletionSoundEnabledChanged(bool value)
    {
        if (_isLoaded)
            preferencesService.IsTaskCompletionSoundEnabled = value;
    }

    partial void OnIsTaskDeleteConfirmationEnabledChanged(bool value)
    {
        if (_isLoaded)
            preferencesService.IsTaskDeleteConfirmationEnabled = value;
    }
}
