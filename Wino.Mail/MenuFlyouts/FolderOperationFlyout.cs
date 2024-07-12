using System.Collections.Generic;
using System.Threading.Tasks;

using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

#if NET8_0
using Microsoft.UI.Xaml.Controls;
#else
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.MenuFlyouts.Context
{
    public class FolderOperationFlyout : WinoOperationFlyout<FolderOperationMenuItem>
    {
        public FolderOperationFlyout(IEnumerable<FolderOperationMenuItem> availableActions, TaskCompletionSource<FolderOperationMenuItem> completionSource) : base(availableActions, completionSource)
        {
            if (AvailableActions == null) return;

            foreach (var action in AvailableActions)
            {
                if (action.Operation == FolderOperation.Seperator)
                    Items.Add(new MenuFlyoutSeparator());
                else
                {
                    var menuFlyoutItem = new FolderOperationMenuFlyoutItem(action, (c) => MenuItemClicked(c));

                    Items.Add(menuFlyoutItem);
                }
            }
        }
    }
}
