using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Wino.Calendar.Controls;

internal sealed partial class CalendarItemControlAutomationPeer(CalendarItemControl owner)
    : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
    protected override string GetClassNameCore() => nameof(CalendarItemControl);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override bool IsControlElementCore() => owner.IsAccessibleEvent;

    protected override bool IsContentElementCore() => owner.IsAccessibleEvent;

    protected override bool IsEnabledCore() => base.IsEnabledCore() && owner.CalendarItem?.IsBusy == false;

    // The event's name contains the details. Decorative glyphs and clipped title text
    // should not produce duplicate or incomplete event announcements.
    protected override List<AutomationPeer> GetChildrenCore() => [];

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

    public void Invoke() => owner.DispatcherQueue.TryEnqueue(owner.OpenAccessibleDetails);
}
