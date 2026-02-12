using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.Views.Abstract;

namespace Wino.Calendar.Views;

public sealed partial class EventDetailsPage : EventDetailsPageAbstract
{
    public EventDetailsPage()
    {
        this.InitializeComponent();
    }

    private void AttachmentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarAttachmentViewModel attachmentViewModel)
        {
            ViewModel?.OpenAttachmentCommand.Execute(attachmentViewModel);
        }
    }

    private void OpenCalendarAttachment_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
        {
            ViewModel?.OpenAttachmentCommand.Execute(attachment);
        }
    }

    private void SaveCalendarAttachment_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
        {
            ViewModel?.SaveAttachmentCommand.Execute(attachment);
        }
    }
}
