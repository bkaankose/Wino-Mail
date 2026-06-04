using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;

namespace Wino.Controls;

public partial class WinoSwipeControlItems : SwipeItems
{
    public static readonly DependencyProperty MailItemProperty = DependencyProperty.Register(nameof(MailItem), typeof(IMailListItem), typeof(WinoSwipeControlItems), new PropertyMetadata(null, new PropertyChangedCallback(OnItemsChanged)));
    public static readonly DependencyProperty IsRightSwipeProperty = DependencyProperty.Register(nameof(IsRightSwipe), typeof(bool), typeof(WinoSwipeControlItems), new PropertyMetadata(false, new PropertyChangedCallback(OnItemsChanged)));

    public IMailListItem MailItem
    {
        get { return (IMailListItem)GetValue(MailItemProperty); }
        set { SetValue(MailItemProperty, value); }
    }

    public bool IsRightSwipe
    {
        get { return (bool)GetValue(IsRightSwipeProperty); }
        set { SetValue(IsRightSwipeProperty, value); }
    }

    private static void OnItemsChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is WinoSwipeControlItems control)
        {
            control.BuildSwipeItems();
        }
    }

    private void BuildSwipeItems()
    {
        var preferencesService = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        var operation = IsRightSwipe ? preferencesService.RightSwipeOperation : preferencesService.LeftSwipeOperation;

        this.Clear();

        var swipeItem = GetSwipeItem(operation);

        Add(swipeItem);
    }

    private SwipeItem? GetSwipeItem(MailOperation operation)
    {
        if (MailItem == null) return null;

        var finalOperation = operation;

        bool isSingleItem = MailItem is MailItemViewModel;

        if (isSingleItem)
        {
            var singleItem = MailItem as MailItemViewModel;

            if (singleItem != null && operation == MailOperation.MarkAsRead && singleItem.IsRead)
                finalOperation = MailOperation.MarkAsUnread;
            else if (singleItem != null && operation == MailOperation.MarkAsUnread && !singleItem.IsRead)
                finalOperation = MailOperation.MarkAsRead;
        }
        else
        {
            var threadItem = MailItem as ThreadMailItemViewModel;

            if (threadItem != null && operation == MailOperation.MarkAsRead && threadItem.ThreadEmails.All(a => a.IsRead))
                finalOperation = MailOperation.MarkAsUnread;
            else if (threadItem != null && operation == MailOperation.MarkAsUnread && threadItem.ThreadEmails.All(a => !a.IsRead))
                finalOperation = MailOperation.MarkAsRead;
        }

        var item = new SwipeItem()
        {
            IconSource = new WinoFontIconSource() { Icon = XamlHelpers.GetWinoIconGlyph(finalOperation) },
            Text = XamlHelpers.GetOperationString(finalOperation),
            BehaviorOnInvoked = SwipeBehaviorOnInvoked.Close,
            CommandParameter = operation
        };

        item.Invoked += SwipeItemInvoked;

        return item;
    }

    private void SwipeItemInvoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (MailItem == null) return;

        var swipeControl = args.SwipeControl;

        // Determine the final operation based on current settings and mail item state
        var finalOperation = (MailOperation)sender.CommandParameter;

        bool isSingleItem = MailItem is MailItemViewModel;

        if (isSingleItem)
        {
            var singleItem = MailItem as MailItemViewModel;

            if (singleItem != null)
            {
                if (finalOperation == MailOperation.MarkAsRead && singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsUnread;
                else if (finalOperation == MailOperation.MarkAsUnread && !singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsRead;
            }
        }
        else
        {
            var threadItem = MailItem as ThreadMailItemViewModel;

            if (threadItem != null && finalOperation == MailOperation.MarkAsRead && threadItem.ThreadEmails.All(a => a.IsRead))
                finalOperation = MailOperation.MarkAsUnread;
            else if (threadItem != null && finalOperation == MailOperation.MarkAsUnread && threadItem.ThreadEmails.All(a => !a.IsRead))
                finalOperation = MailOperation.MarkAsRead;
        }

        // Send message to MailListPageViewModel to handle the operation
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Mail.ViewModels.Messages.SwipeActionRequested(finalOperation, MailItem));
    }
}
