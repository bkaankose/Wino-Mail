using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls
{
    public partial class WinoSwipeControlItems : SwipeItems
    {
        public static readonly DependencyProperty SwipeOperationProperty = DependencyProperty.Register(nameof(SwipeOperation), typeof(MailOperation), typeof(WinoSwipeControlItems), new PropertyMetadata(default(MailOperation), new PropertyChangedCallback(OnItemsChanged)));
        public static readonly DependencyProperty MailItemProperty = DependencyProperty.Register(nameof(MailItem), typeof(IMailItem), typeof(WinoSwipeControlItems), new PropertyMetadata(null));

        public IMailItem MailItem
        {
            get { return (IMailItem)GetValue(MailItemProperty); }
            set { SetValue(MailItemProperty, value); }
        }


        public MailOperation SwipeOperation
        {
            get { return (MailOperation)GetValue(SwipeOperationProperty); }
            set { SetValue(SwipeOperationProperty, value); }
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

        private SwipeItem GetSwipeItem(MailOperation operation)
        {
            if (MailItem == null) return null;

            var finalOperation = operation;

            bool isSingleItem = MailItem is MailItemViewModel;

            if (isSingleItem)
            {
                var singleItem = MailItem as MailItemViewModel;

                if (operation == MailOperation.MarkAsRead && singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsUnread;
                else if (operation == MailOperation.MarkAsUnread && !singleItem.IsRead)
                    finalOperation = MailOperation.MarkAsRead;
            }
            else
            {
                var threadItem = MailItem as ThreadMailItemViewModel;

                if (operation == MailOperation.MarkAsRead && threadItem.ThreadItems.All(a => a.IsRead))
                    finalOperation = MailOperation.MarkAsUnread;
                else if (operation == MailOperation.MarkAsUnread && threadItem.ThreadItems.All(a => !a.IsRead))
                    finalOperation = MailOperation.MarkAsRead;
            }

            var item = new SwipeItem()
            {
                IconSource = new WinoFontIconSource() { Icon = XamlHelpers.GetWinoIconGlyph(finalOperation) },
                Text = XamlHelpers.GetOperationString(finalOperation),
            };

            return item;
        }
    }
}
