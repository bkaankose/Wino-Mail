using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// A value plus its display text for the notification settings combo boxes. Concrete types rather
/// than one generic, because XAML x:DataType cannot name a generic and the project forbids
/// DisplayMemberPath, so every list needs a typed item template.
/// </summary>
public abstract partial class NotificationOptionBase(string displayText)
{
    public string DisplayText { get; } = displayText;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailNotificationScopeOption(MailNotificationScope value, string displayText) : NotificationOptionBase(displayText)
{
    public MailNotificationScope Value { get; } = value;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailNotificationContentOption(MailNotificationContent value, string displayText) : NotificationOptionBase(displayText)
{
    public MailNotificationContent Value { get; } = value;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class NotificationSoundOption(NotificationSoundEvent value, string displayText) : NotificationOptionBase(displayText)
{
    public NotificationSoundEvent Value { get; } = value;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class AccountQuietHoursStanceOption(AccountQuietHoursStance value, string displayText) : NotificationOptionBase(displayText)
{
    public AccountQuietHoursStance Value { get; } = value;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class TaskReminderTimingOption(TaskReminderTiming value, string displayText) : NotificationOptionBase(displayText)
{
    public TaskReminderTiming Value { get; } = value;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailNotificationActionOption(MailOperation operation, string displayText) : NotificationOptionBase(displayText)
{
    public MailOperation Operation { get; } = operation;
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class SnoozePresetOption(NotificationSnoozePreset value, string displayText) : NotificationOptionBase(displayText)
{
    public NotificationSnoozePreset Value { get; } = value;
}

/// <summary>
/// One day toggle in the quiet hours schedule. The label comes from the active culture rather than
/// from translation keys, so it stays correct in every language without seven more resources.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class QuietHoursDayViewModel(DayOfWeek day, string displayText, Action onChanged) : ObservableObject
{
    private readonly Action _onChanged = onChanged;

    public DayOfWeek Day { get; } = day;

    public string DisplayText { get; } = displayText;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

internal static class NotificationOptionLookup
{
    public static TOption Find<TOption, TValue>(IReadOnlyList<TOption> options, TValue value, Func<TOption, TValue> selector)
        where TOption : NotificationOptionBase
        where TValue : struct, Enum
        => options.FirstOrDefault(option => EqualityComparer<TValue>.Default.Equals(selector(option), value)) ?? options[0];
}
