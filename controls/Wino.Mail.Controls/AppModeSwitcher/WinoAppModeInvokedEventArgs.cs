namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// Reports which item the user invoked. The switcher does not change its own selection in
/// response: the host decides whether the mode change actually happened and sets
/// <see cref="WinoAppModeSwitcher.SelectedIndex"/> accordingly.
/// </summary>
public sealed class WinoAppModeInvokedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}
