using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

public enum ContextFlyoutRowKind
{
    Separator,
    Command,
    Toggle,
    Radio,
    SubMenu,
}

/// <summary>
/// One realized line of the flyout. Flattens a <see cref="ContextFlyoutMenuEntry"/> into exactly
/// what the item templates bind, so a search result is the same type with a breadcrumb label
/// instead of a cloned entry.
/// </summary>
public sealed class ContextFlyoutRow
{
    private ContextFlyoutRow(ContextFlyoutMenuEntry entry, ContextFlyoutRowKind kind, string text)
    {
        Entry = entry;
        Kind = kind;
        Text = text;
    }

    public ContextFlyoutMenuEntry Entry { get; }

    public ContextFlyoutRowKind Kind { get; }

    public string Text { get; }

    public string Breadcrumb { get; private init; } = string.Empty;

    public string Glyph { get; private init; } = string.Empty;

    public Brush? IconForeground { get; private init; }

    public string ShortcutText { get; private init; } = string.Empty;

    public bool IsChecked { get; private init; }

    public bool IsEnabled { get; private init; } = true;

    public bool IsDestructive { get; private init; }

    public string AutomationId { get; private init; } = string.Empty;

    /// <summary>
    /// Icon that inherits the item foreground, so it follows the theme and the selected state.
    /// </summary>
    public bool HasThemedIcon => Glyph.Length > 0 && IconForeground is null;

    /// <summary>
    /// Icon painted with the colour carried by the entry, such as a mail category colour.
    /// </summary>
    public bool HasColoredIcon => Glyph.Length > 0 && IconForeground is not null;

    /// <summary>
    /// Command entry behind this row, or <see langword="null"/> for separators and submenus.
    /// </summary>
    public ContextFlyoutCommandEntry? Command => Entry as ContextFlyoutCommandEntry;

    /// <summary>
    /// True when clicking or pressing Enter on the row does something: runs its command, or pages
    /// into a submenu that has children.
    /// </summary>
    public bool CanActivate => Kind switch
    {
        ContextFlyoutRowKind.Separator => false,
        ContextFlyoutRowKind.SubMenu => IsEnabled && ((ContextFlyoutSubMenuEntry)Entry).Items.Count > 0,
        _ => ((ContextFlyoutCommandEntry)Entry).CanExecute(),
    };

    public static ContextFlyoutRow Create(ContextFlyoutMenuEntry entry)
        => Create(entry, displayText: null, breadcrumb: string.Empty);

    /// <summary>
    /// Creates a row for a search result: the same entry rendered with its full breadcrumb path.
    /// </summary>
    public static ContextFlyoutRow CreateSearchResult(ContextFlyoutSearchCandidate candidate)
        => Create(candidate.Source, candidate.DisplayText, candidate.Breadcrumb);

    private static ContextFlyoutRow Create(ContextFlyoutMenuEntry entry, string? displayText, string breadcrumb)
    {
        switch (entry)
        {
            case ContextFlyoutSeparatorEntry:
                return new ContextFlyoutRow(entry, ContextFlyoutRowKind.Separator, string.Empty)
                {
                    IsEnabled = false
                };

            case ContextFlyoutSubMenuEntry subMenu:
                return new ContextFlyoutRow(entry, ContextFlyoutRowKind.SubMenu, displayText ?? subMenu.Text)
                {
                    Breadcrumb = breadcrumb,
                    Glyph = subMenu.Icon?.Glyph ?? string.Empty,
                    IconForeground = CreateIconForeground(subMenu.Icon),
                    IsEnabled = subMenu.IsEnabled,
                    AutomationId = subMenu.AutomationId
                };

            case ContextFlyoutCommandEntry command:
                var kind = command switch
                {
                    ContextFlyoutRadioEntry => ContextFlyoutRowKind.Radio,
                    ContextFlyoutToggleEntry => ContextFlyoutRowKind.Toggle,
                    _ => ContextFlyoutRowKind.Command
                };

                return new ContextFlyoutRow(entry, kind, displayText ?? command.Text)
                {
                    Breadcrumb = breadcrumb,
                    Glyph = command.Icon?.Glyph ?? string.Empty,
                    IconForeground = CreateIconForeground(command.Icon),
                    ShortcutText = command.Shortcut?.DisplayText ?? string.Empty,
                    IsChecked = command is ContextFlyoutToggleEntry { IsChecked: true },
                    IsEnabled = command.IsEnabled,
                    IsDestructive = command.IsDestructive,
                    AutomationId = command.AutomationId
                };

            default:
                throw new NotSupportedException($"Unsupported context flyout entry: {entry.GetType().FullName}");
        }
    }

    /// <summary>
    /// Resolves the optional per-entry icon colour. Entries without one inherit the item
    /// foreground, which keeps them theme-aware.
    /// </summary>
    private static Brush? CreateIconForeground(ContextFlyoutIcon? icon)
        => icon is not null && AccountProfilePictureRenderer.TryParseColor(icon.ForegroundHex, out var skiaColor)
            ? new SolidColorBrush(Color.FromArgb(skiaColor.Alpha, skiaColor.Red, skiaColor.Green, skiaColor.Blue))
            : null;
}
