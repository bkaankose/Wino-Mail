using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Menus;

public class MailOperationMenuItem : MenuOperationItemBase<MailOperation>
{
    private MailOperationMenuItem(MailOperation operation, bool isEnabled, bool isSecondaryMenuItem)
        : base(operation, isEnabled, isSecondaryMenuItem)
    { }

    public static MailOperationMenuItem Create(MailOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false)
        => new MailOperationMenuItem(operation, isEnabled, isSecondaryMenuItem);
}
