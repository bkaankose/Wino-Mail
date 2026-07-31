using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Data;

[DebuggerDisplay("{FolderTitle}")]
public partial class FolderPivotViewModel : ObservableObject
{
    public bool? IsFocused { get; set; }
    public string FolderTitle { get; }

    public bool ShouldDisplaySelectedItemCount => IsExtendedMode ? SelectedItemCount > 1 : SelectedItemCount > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldDisplaySelectedItemCount))]
    public partial int SelectedItemCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldDisplaySelectedItemCount))]
    public partial bool IsExtendedMode { get; set; } = true;

    public FolderPivotViewModel(string folderName, bool? isFocused)
    {
        IsFocused = isFocused;

        FolderTitle = IsFocused == null ? folderName : (IsFocused == true ? Translator.Focused : Translator.Other);
    }

    public override string ToString() => FolderTitle;
}
