namespace Wino.Mail.Controls.Core.ContextFlyout;

public static class ContextFlyoutShortcutPolicy
{
    private static readonly HashSet<string> ControlEditingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "C", "V", "X", "Y", "Z", "Back", "Delete", "Home", "End", "Left", "Right"
    };

    public static bool CanExecuteWhileFiltering(
        string key,
        bool control,
        bool alt,
        bool shift,
        bool windows)
    {
        if (!control && !alt && !shift && !windows)
        {
            return false;
        }

        if (control && !alt && !windows && ControlEditingKeys.Contains(key))
        {
            return false;
        }

        if (!control && !alt && !windows && shift
            && key.Equals("Insert", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
