using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.WinUI.Controls;

namespace Wino.MenuFlyouts.Context;

public partial class FolderOperationFlyout : WinoOperationFlyout<FolderOperationMenuItem>
{
    public FolderOperationFlyout(IEnumerable<FolderOperationMenuItem> availableActions, TaskCompletionSource<FolderOperationMenuItem> completionSource) : base(availableActions, completionSource)
    {
        if (AvailableActions == null)
            return;

        var items = new List<ContextFlyoutMenuEntry>();
        foreach (var action in AvailableActions)
        {
            if (action.Operation == FolderOperation.Seperator)
            {
                items.Add(ContextFlyoutSeparatorEntry.Instance);
            }
            else
            {
                items.Add(new ContextFlyoutCommandEntry
                {
                    Text = XamlHelpers.GetOperationString(action.Operation),
                    Icon = CreateIcon(action.Operation),
                    IsEnabled = action.IsEnabled,
                    IsDestructive = action.Operation is FolderOperation.Delete or FolderOperation.Empty,
                    Command = new RelayCommand(() => MenuItemClicked(action), () => action.IsEnabled),
                    AutomationId = $"FolderContext{action.Operation}"
                });
            }
        }

        ItemsSource = items;
    }

    private static ContextFlyoutIcon? CreateIcon(FolderOperation operation)
        => new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(XamlHelpers.GetPathGeometry(operation)));
}
