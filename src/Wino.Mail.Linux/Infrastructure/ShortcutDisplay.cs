using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>How a stored shortcut is shown: keycaps for a combination GTK knows, plain text otherwise (a Mac Command shortcut).</summary>
internal static class ShortcutDisplay
{
    public static Gtk.Widget Create(ModifierKeys modifiers, string? storedKey)
    {
        if (ShortcutKeys.Accelerator(modifiers, storedKey) is { } accelerator)
        {
            var keys = Adw.ShortcutLabel.New(accelerator);
            keys.SetValign(Gtk.Align.Center);
            return keys;
        }
        var label = Gtk.Label.New(Text(modifiers, storedKey));
        label.AddCssClass("dim-label");
        label.SetValign(Gtk.Align.Center);
        return label;
    }

    /// <summary>"Ctrl+Shift+G" for accessible names and logs.</summary>
    public static string Text(ModifierKeys modifiers, string? storedKey)
    {
        var keyval = ShortcutKeys.Keyval(storedKey);
        if (keyval != 0 && !modifiers.HasFlag(ModifierKeys.Command))
            return Gtk.Functions.AcceleratorGetLabel(keyval, ShortcutKeys.State(modifiers));

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Super");
        if (modifiers.HasFlag(ModifierKeys.Command)) parts.Add("⌘");
        parts.Add(ShortcutKeys.Normalize(storedKey));
        return string.Join("+", parts);
    }
}
