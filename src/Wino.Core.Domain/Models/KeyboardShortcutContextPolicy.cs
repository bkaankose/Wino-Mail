using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models;

public static class KeyboardShortcutContextPolicy
{
    public static bool CanExecute(
        KeyboardShortcutAction action,
        string key,
        ModifierKeys modifierKeys,
        KeyboardShortcutInputContext inputContext,
        bool isTextInput)
    {
        if (inputContext is KeyboardShortcutInputContext.Compose or KeyboardShortcutInputContext.PopOutCompose)
            return action == KeyboardShortcutAction.Send;

        if (!isTextInput)
            return true;

        if (action == KeyboardShortcutAction.Delete || modifierKeys == ModifierKeys.None)
            return false;

        if (!modifierKeys.HasFlag(ModifierKeys.Control))
            return true;

        return key.ToUpperInvariant() is not ("A" or "B" or "C" or "F" or "I" or "K" or "S" or "U" or "V" or "X" or "Y" or "Z");
    }
}
