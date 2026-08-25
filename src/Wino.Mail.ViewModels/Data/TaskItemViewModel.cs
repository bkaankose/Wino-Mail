using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One row in the task list, and the subject of the detail drawer.
/// <para>
/// <see cref="AccountTask"/> is a plain SQLite entity with no change notification, so before this
/// wrapper existed every star or checkbox had to reload the whole surface to repaint. Toggles write
/// through to the entity and raise notifications locally; the owning view model persists in the
/// background and reverts this object if the write fails.
/// </para>
/// </summary>
public partial class TaskItemViewModel : ObservableObject
{
    public AccountTask Task { get; }

    public ObservableCollection<TaskStepViewModel> Steps { get; } = [];

    public TaskItemViewModel(AccountTask task, string listName)
    {
        Task = task;
        ListName = listName ?? string.Empty;

        foreach (var step in task.Steps ?? [])
            Steps.Add(new TaskStepViewModel(step));

        Steps.CollectionChanged += (_, _) => RefreshStepSummary();
    }

    public Guid Id => Task.Id;

    public string ListName { get; }

    public string Title
    {
        get => Task.Title;
        set
        {
            if (Task.Title == value) return;

            Task.Title = value;
            OnPropertyChanged();
        }
    }

    public string Notes
    {
        get => Task.Notes;
        set
        {
            if (Task.Notes == value) return;

            Task.Notes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool IsCompleted
    {
        get => Task.IsCompleted;
        set
        {
            if (Task.IsCompleted == value) return;

            Task.IsCompleted = value;
            Task.CompletedAtUtc = value ? DateTime.UtcNow : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsImportant
    {
        get => Task.IsImportant;
        set
        {
            if (Task.IsImportant == value) return;

            Task.IsImportant = value;
            OnPropertyChanged();
        }
    }

    public DateTime? DueDate
    {
        get => Task.DueDate;
        set
        {
            var normalized = value?.Date;
            if (Task.DueDate == normalized) return;

            Task.DueDate = normalized;
            OnPropertyChanged();
            RefreshDueDisplay();
        }
    }

    /// <summary>
    /// The day this task was pulled into My Day. Membership is "this is today", so no timer or
    /// background job is needed to empty the list overnight.
    /// </summary>
    public DateTime? MyDayDateUtc
    {
        get => Task.MyDayDateUtc;
        set
        {
            var normalized = value?.Date;
            if (Task.MyDayDateUtc == normalized) return;

            Task.MyDayDateUtc = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInMyDay));
            OnPropertyChanged(nameof(MyDayActionText));
        }
    }

    public bool IsInMyDay => Task.MyDayDateUtc == DateTime.UtcNow.Date;
    public bool IsActive => !Task.IsCompleted;
    public bool IsReadOnly => Task.IsReadOnly;
    public bool IsEditable => !Task.IsReadOnly;

    public string MyDayActionText => IsInMyDay ? Translator.ToDoPage_AddedToMyDay : Translator.ToDoPage_AddToMyDay;
    public string ImportanceActionText => IsImportant ? Translator.ToDoPage_RemoveImportance : Translator.ToDoPage_MarkImportant;

    public bool HasNote => !string.IsNullOrWhiteSpace(Task.Notes);
    public bool HasDueDate => Task.DueDate.HasValue;
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Relative due text: "Due today", "Due Friday", "3 days overdue".</summary>
    public string DueDisplayText
    {
        get
        {
            if (Task.DueDate is not { } due)
                return string.Empty;

            var days = (due.Date - DateTime.Now.Date).Days;
            return days switch
            {
                < -1 => string.Format(Translator.ToDoPage_DueOverdue, Math.Abs(days)),
                -1 => Translator.ToDoPage_DueYesterday,
                0 => Translator.ToDoPage_DueToday,
                1 => Translator.ToDoPage_DueTomorrow,
                < 7 => string.Format(Translator.ToDoPage_DueOn, due.ToString("dddd")),
                _ => string.Format(Translator.ToDoPage_DueOn, due.ToString("MMM d"))
            };
        }
    }

    /// <summary>Overdue only matters while the task is still open.</summary>
    public bool IsOverdue => !Task.IsCompleted && Task.DueDate is { } due && due.Date < DateTime.Now.Date;

    public string StepSummaryText
        => Steps.Count == 0
            ? string.Empty
            : string.Format(Translator.ToDoPage_StepSummary, Steps.Count(step => step.IsCompleted), Steps.Count);

    public string CreatedOnText
        => string.Format(Translator.ToDoPage_CreatedOn, Task.CreatedAtUtc.ToLocalTime().ToString("ddd, MMMM d"));

    /// <summary>Metadata line label for the owning list. Hidden when the surface is a single list.</summary>
    public bool ShowListName { get; set; }

    public void RefreshStepSummary()
    {
        OnPropertyChanged(nameof(StepSummaryText));
        OnPropertyChanged(nameof(HasSteps));
    }

    public void RefreshDueDisplay()
    {
        OnPropertyChanged(nameof(DueDisplayText));
        OnPropertyChanged(nameof(HasDueDate));
        OnPropertyChanged(nameof(IsOverdue));
    }
}
