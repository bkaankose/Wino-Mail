using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Domain.Models.Menus;
using Wino.Domain.Enums;




#if NET8_0
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml.Controls;
#endif
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
