using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailCal.Mail.Controls.Core;
using MailCal.Mail.Controls.Core.HoverActions;

namespace MailCal.Mail.Controls.Playground.Pages;

public sealed partial class HoverActionsPageViewModel : ObservableObject
{
    public MailListRow Row { get; } = MailListRow.Single(new PlaygroundHoverActionItem());

    public HoverActionLabels Labels { get; } =
        new("Archive", "Delete", "Flag / Unflag", "Read / Unread", "Move to Junk");

    [ObservableProperty]
    public partial string StatusText { get; set; } = "No action invoked";

    [RelayCommand]
    private void InvokeAction(HoverActionCommandRequest request) =>
        StatusText = $"Invoked {request.Action} for {request.Row.SourceItem.NameSortKey}";
}
