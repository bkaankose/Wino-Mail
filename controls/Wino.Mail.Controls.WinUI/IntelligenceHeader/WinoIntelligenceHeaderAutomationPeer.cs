using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Wino.Mail.Controls.IntelligenceHeader;

/// <summary>
/// Exposes <see cref="WinoIntelligenceHeader"/> as a named group while its buttons and lists
/// retain their native automation patterns.
/// </summary>
public sealed partial class WinoIntelligenceHeaderAutomationPeer(WinoIntelligenceHeader owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(WinoIntelligenceHeader);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetNameCore()
    {
        var name = AutomationProperties.GetName(owner);
        return string.IsNullOrWhiteSpace(name) ? owner.HeaderTitle : name;
    }
}
