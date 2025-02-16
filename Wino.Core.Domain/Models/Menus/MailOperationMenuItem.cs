using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Menus
{
    public class MailOperationMenuItem : MenuOperationItemBase<MailOperation>, IMenuOperation
    {
        /// <summary>
        /// Gets or sets whether this menu item should be placed in SecondaryCommands if used in CommandBar.
        /// </summary>
        public bool IsSecondaryMenuPreferred { get; set; }

        protected MailOperationMenuItem(MailOperation operation, bool isEnabled, bool isSecondaryMenuItem = false) : base(operation, isEnabled)
        {
            IsSecondaryMenuPreferred = isSecondaryMenuItem;
        }

        public static MailOperationMenuItem Create(MailOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false)
            => new MailOperationMenuItem(operation, isEnabled, isSecondaryMenuItem);
    }
}
