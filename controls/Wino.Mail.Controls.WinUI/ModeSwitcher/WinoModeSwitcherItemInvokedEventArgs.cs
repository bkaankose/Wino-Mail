namespace Wino.Mail.Controls.ModeSwitcher;

/// <summary>
/// Raised when the user picks a mode by pointer or keyboard. Programmatic
/// <see cref="WinoModeSwitcher.SelectedItem"/> changes do not raise it.
/// </summary>
public sealed class WinoModeSwitcherItemInvokedEventArgs(WinoModeSwitcherItem item) : EventArgs
{
    public WinoModeSwitcherItem Item { get; } = item;
}
