using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The notice bar of the To Do page for results of its own actions (copy, import, export, a next occurrence
/// that could not be created). It sits over the task list beside the Undo bar, so a long message wraps
/// inside the page instead of running off the shell's corner.
/// </summary>
public partial class ToDoPageViewModel
{
    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarMessageType NoticeSeverity { get; set; }

    private void ShowNotice(string title, string message, InfoBarMessageType severity)
        => _ = ExecuteUIThread(() =>
        {
            NoticeTitle = title ?? string.Empty;
            NoticeMessage = message ?? string.Empty;
            NoticeSeverity = severity;
            IsNoticeOpen = true;
        });

    [RelayCommand]
    private void CloseNotice() => IsNoticeOpen = false;
}
