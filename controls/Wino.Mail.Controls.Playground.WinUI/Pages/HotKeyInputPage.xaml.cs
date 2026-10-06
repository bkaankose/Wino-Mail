using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.HotKeyInput;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class HotKeyInputPage : Page
{
    public HotKeyInputPage() => InitializeComponent();

    private void HotKeyInput_HotKeyCommitted(object? sender, HotKeyCommittedEventArgs e)
    {
        HotKeyInput.Key = e.Key;
        HotKeyInput.Modifiers = e.Modifiers;
        CommittedValue.Text = $"Committed: {WinoHotKeyInput.Format(e.Key, e.Modifiers)}";
    }
}
