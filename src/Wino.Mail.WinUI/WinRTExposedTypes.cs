using Wino.Calendar.ViewModels.Data;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;
using WinRT;

// Wino.Mail.ViewModels is a portable net10.0 library without CsWinRT, so the CCW vtables for its
// types handed to WinUI are generated here. A shared type that reaches an items source or a
// WinUI property without being listed gets no vtable and fails only in the Native AOT build.
// Wino.Mail.Controls.Core types are listed in Wino.Mail.Controls.WinUI, which both apps load.

[assembly: GeneratedWinRTExposedExternalType(typeof(AccountCalendarViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(GroupedAccountCalendarViewModel))]

[assembly: GeneratedWinRTExposedExternalType(typeof(MailNotificationScopeOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailNotificationContentOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(NotificationSoundOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountQuietHoursStanceOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(TaskReminderTimingOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailNotificationActionOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SnoozePresetOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(QuietHoursDayViewModel))]

[assembly: GeneratedWinRTExposedExternalType(typeof(AliasManagementItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AliasManagementItemList))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AliasCertificateList))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(TaskGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ThreadMailItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListCollection<MailItemViewModel>))]
