using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IShortcutPlatformService
{
    ModifierKeys PrimaryCommandModifier { get; }
    bool IsShiftKeyPressed();
}
