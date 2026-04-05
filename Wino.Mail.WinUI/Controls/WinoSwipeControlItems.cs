using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls;

public partial class WinoSwipeControlItems : SwipeItems
{
    public static readonly DependencyProperty SwipeOperationProperty = DependencyProperty.Register(nameof(SwipeOperation), typeof(MailOperation), typeof(WinoSwipeControlItems), new PropertyMetadata(default(MailOperation), new PropertyChangedCallback(OnItemsChanged)));
    public static readonly DependencyProperty MailItemProperty = DependencyProperty.Register(nameof(MailItem), typeof(IMailListItem), typeof(WinoSwipeControlItems), new PropertyMetadata(null));
    public static readonly DependencyProperty IsRightSwipeProperty = DependencyProperty.Register(nameof(IsRightSwipe), typeof(bool), typeof(WinoSwipeControlItems), new PropertyMetadata(false, new PropertyChangedCallback(OnItemsChanged)));

    public WinoSwipeControlItems()
    {
        var preferencesService = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        SwipeOperation = IsRightSwipe ? preferencesService.RightSwipeOperation : preferencesService.LeftSwipeOperation;
    }

    public IMailListItem MailItem
    {
        get { return (IMailListItem)GetValue(MailItemProperty); }
        set { SetValue(MailItemProperty, value); }
    }


    public MailOperation SwipeOperation
    {
        get { return (MailOperation)GetValue(SwipeOperationProperty); }
        set { SetValue(SwipeOperationProperty, value); }
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
        this.Clear();

        var swipeItem = GetSwipeItem(SwipeOperation);

        this.Add(swipeItem);
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
        };

        item.Invoked += SwipeItemInvoked;

        return item;
    }

    private void SwipeItemInvoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        var swipeControl = args.SwipeControl;
        swipeControl.Close();

        if (MailItem == null) return;

        // Determine the final operation based on current settings and mail item state
        var finalOperation = SwipeOperation;

        bool isSingleItem = MailItem is MailItemViewModel;

        if (isSingleItem)
        {
            var singleItem = MailItem as MailItemViewModel;

            if (singleItem != null)
            {
                if (SwipeOperation == MailOperation.MarkAsRead && singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsUnread;
                else if (SwipeOperation == MailOperation.MarkAsUnread && !singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsRead;
            }
        }
        else
        {
            var threadItem = MailItem as ThreadMailItemViewModel;

            if (threadItem != null && SwipeOperation == MailOperation.MarkAsRead && threadItem.ThreadEmails.All(a => a.IsRead))
                finalOperation = MailOperation.MarkAsUnread;
            else if (threadItem != null && SwipeOperation == MailOperation.MarkAsUnread && threadItem.ThreadEmails.All(a => !a.IsRead))
                finalOperation = MailOperation.MarkAsRead;
        }

        // Send message to MailListPageViewModel to handle the operation
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Mail.ViewModels.Messages.SwipeActionRequested(finalOperation, MailItem));
    }
}
