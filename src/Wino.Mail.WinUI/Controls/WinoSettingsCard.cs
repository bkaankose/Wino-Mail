using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Keeps the toolkit card's template and interactions, while providing a working
/// Invoke provider for its clickable state.
/// </summary>
public sealed partial class WinoSettingsCard : SettingsCard
{
    public event RoutedEventHandler? AutomationInvoked;

    protected override AutomationPeer OnCreateAutomationPeer() => new WinoSettingsCardAutomationPeer(this);

    internal void InvokeFromAutomation()
    {
        if (!IsEnabled || !IsClickEnabled) return;

        if (Command is { } command)
        {
            if (command.CanExecute(CommandParameter)) command.Execute(CommandParameter);
        }
        else
        {
            // Click-based pages use the same handler for pointer and automation activation.
            AutomationInvoked?.Invoke(this, new RoutedEventArgs());
        }
    }
}

internal sealed partial class WinoSettingsCardAutomationPeer(WinoSettingsCard owner)
    : SettingsCardAutomationPeer(owner), IInvokeProvider
{
    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Invoke && owner.IsClickEnabled ? this : base.GetPatternCore(patternInterface);

    public void Invoke()
    {
        if (!owner.IsEnabled || !owner.IsClickEnabled) throw new ElementNotEnabledException();
        owner.DispatcherQueue.TryEnqueue(owner.InvokeFromAutomation);
    }
}
