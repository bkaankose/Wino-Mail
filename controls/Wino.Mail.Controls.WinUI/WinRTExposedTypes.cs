using System.Collections.Generic;
using System.Collections.ObjectModel;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using WinRT;

// Wino.Mail.Controls.Core is a portable net10.0 library without CsWinRT, so the CCW vtables for its
// types handed to WinUI are generated here. This library is loaded by both the app and the
// playground, so one list serves both. A closed MailListCollection<TItem> is listed by the app
// that names TItem.

[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutIcon))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutShortcut))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutSeparatorEntry))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutCommandEntry))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutToggleEntry))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutRadioEntry))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutSubMenuEntry))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContextFlyoutHeaderEntry))]

[assembly: GeneratedWinRTExposedExternalType(typeof(MailListProjectionOptions))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListRow))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListGroup))]

// Collections the Release build of Wino.Mail.Controls.Core generated for itself while it still
// carried CsWinRT. They are listed to keep the marshalling data the app shipped with.

[assembly: GeneratedWinRTExposedExternalType(typeof(List<MailListRow>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListRow[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(IMailListSourceItem[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(HoverActionKind[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<WinoIntelligenceTile>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyCollection<WinoIntelligenceTile>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ContextFlyoutSearchCandidate>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyCollection<ContextFlyoutSearchCandidate>))]
