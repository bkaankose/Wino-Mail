using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Menus;

namespace Wino.MenuFlyouts.Context
{
    public class MailOperationFlyout : WinoOperationFlyout<MailOperationMenuItem>
    {
        public MailOperationFlyout(IEnumerable<MailOperationMenuItem> availableActions, TaskCompletionSource<MailOperationMenuItem> completionSource) : base(availableActions, completionSource)
        {
            if (AvailableActions == null) return;

            foreach (var action in AvailableActions)
            {
                if (action.Operation == MailOperation.Seperator)
                    Items.Add(new MenuFlyoutSeparator());
                else
                {
                    var menuFlyoutItem = new MailOperationMenuFlyoutItem(action, (c) => MenuItemClicked(c));

                    Items.Add(menuFlyoutItem);
                }
            }
        }
    }
}
