using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IShortcutPlatformService
{
    ModifierKeys PrimaryCommandModifier { get; }
    bool IsShiftKeyPressed();
}
