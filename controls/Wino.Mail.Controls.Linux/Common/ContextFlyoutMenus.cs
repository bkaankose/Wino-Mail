using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Common;

/// <summary>
/// Turns the shared context flyout entries (the Windows <c>WinoContextFlyout</c> model) into Wino menu items with
/// their glyphs, accelerators and destructive flag.
/// </summary>
public static class ContextFlyoutMenus
{
    public static void AppendHeaders(WinoMenuBuilder builder, IReadOnlyList<ContextFlyoutHeaderEntry> headers)
    {
        var added = false;
        foreach (var header in headers)
        {
            if (string.IsNullOrEmpty(header.Label)) continue;
            var command = header.Command;
            var parameter = header.CommandParameter;
            builder.Item(header.Label, () => Execute(command, parameter), header.CanExecute(), Accelerator(header.Shortcut), Icon(header.Icon));
            added = true;
        }
        if (added) builder.Separator();
    }

    public static void Append(WinoMenuBuilder builder, IReadOnlyList<ContextFlyoutMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ContextFlyoutSeparatorEntry:
                    builder.Separator();
                    break;
                case ContextFlyoutSubMenuEntry subMenu:
                    builder.Submenu(subMenu.Text, child => Append(child, subMenu.Items), subMenu.IsEnabled, Icon(subMenu.Icon));
                    break;
                case ContextFlyoutRadioEntry radio:
                    {
                        var command = radio.Command;
                        var parameter = radio.CommandParameter;
                        if (radio.CanExecute()) builder.Radio(GroupKey(radio.GroupName), radio.Text, radio.IsChecked, () => Execute(command, parameter), Accelerator(radio.Shortcut), Icon(radio.Icon));
                        else builder.Item(radio.Text, static () => { }, enabled: false, Accelerator(radio.Shortcut), Icon(radio.Icon));
                        break;
                    }
                case ContextFlyoutToggleEntry toggle:
                    {
                        var command = toggle.Command;
                        var parameter = toggle.CommandParameter;
                        builder.Check(toggle.Text, toggle.IsChecked, _ => Execute(command, parameter), toggle.CanExecute(), Accelerator(toggle.Shortcut), Icon(toggle.Icon));
                        break;
                    }
                case ContextFlyoutCommandEntry item:
                    {
                        var command = item.Command;
                        var parameter = item.CommandParameter;
                        builder.Item(item.Text, () => Execute(command, parameter), item.CanExecute(), Accelerator(item.Shortcut), Icon(item.Icon), item.IsDestructive);
                        break;
                    }
            }
        }
    }

    /// <summary>The GTK accelerator for a shared shortcut, whose key uses the Windows <c>VirtualKey</c> names.</summary>
    public static string? Accelerator(ContextFlyoutShortcut? shortcut)
    {
        if (shortcut is null || string.IsNullOrWhiteSpace(shortcut.Key)) return null;
        return WinoMenus.Accelerator(GdkKeyName(shortcut.Key), shortcut.Control, shortcut.Alt, shortcut.Shift, shortcut.Windows);
    }

    private static string GdkKeyName(string key) => key switch
    {
        { Length: 1 } when char.IsLetter(key[0]) => key.ToLowerInvariant(),
        { Length: 7 } when key.StartsWith("Number", StringComparison.Ordinal) && char.IsDigit(key[6]) => key[6..],
        "Back" => "BackSpace",
        "Enter" => "Return",
        "Space" => "space",
        "PageUp" => "Page_Up",
        "PageDown" => "Page_Down",
        "Add" => "plus",
        "Subtract" => "minus",
        _ => key,
    };

    private static WinoMenuIcon Icon(ContextFlyoutIcon? icon) => icon is { } value ? new WinoMenuIcon(value.Glyph, value.ForegroundHex) : default;

    private static string GroupKey(string groupName) => string.IsNullOrEmpty(groupName) ? "default" : groupName;

    private static void Execute(System.Windows.Input.ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }
}
