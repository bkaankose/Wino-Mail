using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Linux seeds Mail's Forward as Ctrl+L once, as a configurable shortcut so Settings and the menu hints show it; the
/// shared defaults have no Forward key. A user who deletes the row keeps it deleted, until Reset to defaults seeds it again.
/// </summary>
internal static class ShortcutSeeds
{
    public const string ForwardSeededKey = "LinuxForwardShortcutSeeded";
    private const string ForwardKey = "L";

    public static async Task SeedAsync(IKeyboardShortcutService shortcuts, IConfigurationService configuration, bool force)
    {
        if (!force && configuration.Get(ForwardSeededKey, false)) return;
        configuration.Set(ForwardSeededKey, true);
        var existing = await shortcuts.GetKeyboardShortcutsAsync();
        if (existing.Any(static shortcut => shortcut.Mode == WinoApplicationMode.Mail && shortcut.Action == KeyboardShortcutAction.Forward)) return;
        if (await shortcuts.IsKeyCombinationInUseAsync(WinoApplicationMode.Mail, ForwardKey, ModifierKeys.Control)) return;
        await shortcuts.SaveKeyboardShortcutAsync(new KeyboardShortcut
        {
            Mode = WinoApplicationMode.Mail,
            Key = ForwardKey,
            ModifierKeys = ModifierKeys.Control,
            Action = KeyboardShortcutAction.Forward,
            IsEnabled = true
        });
        Serilog.Log.Information("Keyboard shortcuts: Forward was seeded as Ctrl+L.");
    }
}
