using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;
using WinRT;

// Closed forms of the portable MailListCollection<TItem> that this app hands to WinUI. The other
// Wino.Mail.Controls.Core types are listed in Wino.Mail.Controls.WinUI.

[assembly: GeneratedWinRTExposedExternalType(typeof(MailListCollection<MailListPlaygroundItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListCollection<MailListLabItem>))]
