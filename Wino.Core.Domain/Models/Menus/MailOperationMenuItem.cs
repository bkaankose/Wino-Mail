using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Menus;

public class MailOperationMenuItem : MenuOperationItemBase<MailOperation>
{
    protected MailOperationMenuItem(MailOperation operation, bool isEnabled, bool isSecondaryMenuItem = false) : base(operation, isEnabled)
    {
        IsSecondaryMenuPreferred = isSecondaryMenuItem;
    }

    public static MailOperationMenuItem Create(MailOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false)
        => new MailOperationMenuItem(operation, isEnabled, isSecondaryMenuItem);
}
