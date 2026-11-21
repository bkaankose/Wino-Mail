using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Menus;

public class MailOperationMenuItem : MenuOperationItemBase<MailOperation>
{
    private MailOperationMenuItem(MailOperation operation, bool isEnabled, bool isSecondaryMenuItem, MailOperationMenuPlacement placement)
        : base(operation, isEnabled, isSecondaryMenuItem)
    {
        Placement = placement;
    }

    /// <summary>Where a context menu shows the item. Consecutive items with the same submenu placement form one submenu.</summary>
    public MailOperationMenuPlacement Placement { get; }

    public static MailOperationMenuItem Create(MailOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false, MailOperationMenuPlacement placement = MailOperationMenuPlacement.Menu)
        => new MailOperationMenuItem(operation, isEnabled, isSecondaryMenuItem, placement);
}
