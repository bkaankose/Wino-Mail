using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One checklist step inside the task detail drawer. The entity is a plain SQLite row with no
/// change notification, so the drawer binds to this wrapper instead of reloading the task.
/// </summary>
public partial class TaskStepViewModel : ObservableObject
{
    public AccountTaskStep Step { get; }

    public TaskStepViewModel(AccountTaskStep step) => Step = step;

    public string Title
    {
        get => Step.Title;
        set
        {
            if (Step.Title == value) return;

            Step.Title = value;
            OnPropertyChanged();
        }
    }

    public bool IsCompleted
    {
        get => Step.IsCompleted;
        set
        {
            if (Step.IsCompleted == value) return;

            Step.IsCompleted = value;
            OnPropertyChanged();
        }
    }

    public bool IsReadOnly => Step.IsReadOnly;
    public bool IsEditable => !Step.IsReadOnly;
}
