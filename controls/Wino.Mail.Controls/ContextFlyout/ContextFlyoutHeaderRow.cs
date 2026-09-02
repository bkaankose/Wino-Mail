using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// One header command of the flyout. The presenter invokes it, so the entry's command runs before
/// the flyout is dismissed.
/// </summary>
public sealed class ContextFlyoutHeaderRow
{
    private ContextFlyoutHeaderRow(ContextFlyoutHeaderEntry entry)
    {
        Entry = entry;
        Label = entry.Label;
        Glyph = entry.Icon?.Glyph ?? string.Empty;
        IsEnabled = entry.CanExecute();
        AutomationId = entry.AutomationId;
        ToolTip = string.IsNullOrWhiteSpace(entry.Shortcut?.DisplayText)
            ? entry.Label
            : $"{entry.Label} ({entry.Shortcut!.DisplayText})";
    }

    public ContextFlyoutHeaderEntry Entry { get; }

    public string Label { get; }

    public string ToolTip { get; }

    public string Glyph { get; }

    public bool IsEnabled { get; }

    public string AutomationId { get; }

    public static ContextFlyoutHeaderRow Create(ContextFlyoutHeaderEntry entry) => new(entry);
}
