using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.FlaggedMail;
using Wino.Mail.ViewModels.MenuItems;

namespace Wino.Mail.ViewModels;

public partial class ToDoPageViewModel
{
    private readonly FlaggedMailTaskMenuItem _flaggedMailMenuItem = new();

    /// <summary>Flagged mail of every account, listed as a smart view; null on a host without it.</summary>
    public FlaggedMailListViewModel FlaggedMail { get; }

    public bool IsFlaggedMailAvailable => FlaggedMail?.IsAvailable == true;

    /// <summary>The Flagged mail view replaces the task list and the quick add.</summary>
    public bool IsFlaggedMailSelected => IsFlaggedMailAvailable && SelectedList is null && SelectedView == TaskViewKind.FlaggedMail;

    public bool IsTaskSurfaceVisible => !IsFlaggedMailSelected;

    public bool IsTaskEmptyStateVisible => IsEmpty && !IsFlaggedMailSelected;

    private void OnFlaggedMailSurfaceChanged()
    {
        OnPropertyChanged(nameof(IsFlaggedMailSelected));
        OnPropertyChanged(nameof(IsTaskSurfaceVisible));
        OnPropertyChanged(nameof(IsTaskEmptyStateVisible));

        if (IsFlaggedMailSelected)
            _ = FlaggedMail.ReloadAsync();
    }
}
