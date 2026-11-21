using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Companion;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.FlaggedMail;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Mail.ViewModels.Links;
using Wino.Mail.ViewModels.Search;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.Calendar.Services;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;
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
[assembly: GeneratedWinRTExposedExternalType(typeof(ReminderOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<ReminderOption>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarReminderEditorRow))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<CalendarReminderEditorRow>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailNotificationActionOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SnoozePresetOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(QuietHoursDayViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AppLockMethodOption))]

[assembly: GeneratedWinRTExposedExternalType(typeof(AliasManagementItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AliasManagementItemList))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AliasCertificateList))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(TaskGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ThreadMailItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailListCollection<MailItemViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountProviderDetailViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MergedAccountProviderDetailViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<IAccountProviderDetailViewModel>))]

// Mail pane menu items. They moved here from Wino.Core.Domain, which never generated marshalling data.

[assembly: GeneratedWinRTExposedExternalType(typeof(MenuItemCollection))]
[assembly: GeneratedWinRTExposedExternalType(typeof(NewMailMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SeperatorItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MergedAccountMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountGroupMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(FixAccountIssuesMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(FolderMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MergedAccountFolderMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailCategoryMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MergedMailCategoryMenuItem))]

// Types the Release build of Wino.Mail.ViewModels generated for itself while it still carried
// CsWinRT. They are listed to keep the marshalling data the app shipped with.

[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarAppShellViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarComposeWeekdayOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(RsvpStatusOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountCalendarGroupMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarAccountMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarAttachmentViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountCalendarStateService))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ShellSectionHeaderMenuItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AppearanceSettingsModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CalendarBaseViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SettingsDialogViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SettingsMenuProvider))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountNotificationSettingsViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AppModeOptionViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(WinoAccountBenefitItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(DateGroupHeader))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SenderGroupHeader))]
[assembly: GeneratedWinRTExposedExternalType(typeof(CompanionDashboardViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountSetupAppRow))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountSetupConnectionCheck))]
[assembly: GeneratedWinRTExposedExternalType(typeof(BreadcrumbNavigationItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ComposerRewriteSession))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactCategoryMembershipViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactFilterGroup))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactListMembershipViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(FolderCustomizationItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(IntelligenceFolderCoverageItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(IntelligenceFolderNode))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailAttachmentViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(RemoteContentModeOption))]
[assembly: GeneratedWinRTExposedExternalType(typeof(RemoteContentRuleViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SemanticIndexRangeBucketViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(TaskStepViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(VoiceBrainFactItem))]
[assembly: GeneratedWinRTExposedExternalType(typeof(WelcomeWizardContext))]
[assembly: GeneratedWinRTExposedExternalType(typeof(WinoIntelligenceHeaderPresenter))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailAppShellViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailBaseViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(UnreadBadgeFolderViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailSearchFilterEditor))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableRangeCollection<string>))]

[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableObject[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AppLockMethodOption[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<AccountCalendarViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountCalendarViewModel[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<GroupedAccountCalendarViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(GroupedAccountCalendarViewModel[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<CalendarComposeAttendeeViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<CalendarEventAttendee>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ContactList>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyCollection<ContactList>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactList[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailCategory[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<IMenuItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(IMenuItem[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<IMenuOperation>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyCollection<IMenuOperation>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(MailOperationMenuItem[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ContactCreateDestination>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<X509Certificate2>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(X509Certificate2[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<CompanionEventItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<AccountContactViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(AccountContactViewModel[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<ContactFilterViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyCollection<ContactFilterViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ContactFilterViewModel[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<IntelligenceFolderNode>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<MailItemViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<TaskItemViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(TaskItemViewModel[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<TaskListPreferenceOption>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(List<MailSearchFilterChip>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(KnownImapProviderRegion[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(SettingsNavigationItemInfo[]))]
[assembly: GeneratedWinRTExposedExternalType(typeof(BriefingLabelChip[]))]

// Items linked to the open message, task or event.
[assembly: GeneratedWinRTExposedExternalType(typeof(LinkedItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<LinkedItemViewModel>))]

// The Flagged mail view of To Do.
[assembly: GeneratedWinRTExposedExternalType(typeof(FlaggedMailItemViewModel))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableCollection<FlaggedMailItemViewModel>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(FlaggedMailTaskMenuItem))]
