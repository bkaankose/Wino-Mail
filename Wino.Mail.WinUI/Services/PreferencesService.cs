using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Translations;
using Wino.Services;

namespace Wino.Mail.WinUI.Services;

public class PreferencesService(IConfigurationService configurationService) : ObservableObject, IPreferencesService
{
    private readonly IConfigurationService _configurationService = configurationService;

    public event EventHandler<string>? PreferenceChanged;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        PreferenceChanged?.Invoke(this, e.PropertyName ?? string.Empty);
    }

    private void SaveProperty(string propertyName, object? value) => _configurationService.Set(propertyName, value ?? string.Empty);

    private void SetPropertyAndSave(string propertyName, object? value)
    {
        _configurationService.Set(propertyName, value ?? string.Empty);

        OnPropertyChanged(propertyName);
    }

    public MailRenderingOptions GetRenderingOptions()
        => new MailRenderingOptions()
        {
            LoadImages = RenderImages,
            LoadStyles = RenderStyles,
            RenderPlaintextLinks = RenderPlaintextLinks
        };

    public string ExportPreferences()
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in GetSyncablePreferenceProperties())
        {
            settings[property.Name] = property.GetValue(this);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var setting in settings)
            {
                WritePreferenceValue(writer, setting.Key, setting.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public (int appliedCount, int failedCount) ImportPreferences(string settingsJson)
    {
        using var document = JsonDocument.Parse(settingsJson);
        var rootElement = document.RootElement;
        var appliedCount = 0;
        var failedCount = 0;

        foreach (var property in GetSyncablePreferenceProperties())
        {
            if (!rootElement.TryGetProperty(property.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            try
            {
                property.SetValue(this, ReadPreferenceValue(property.PropertyType, value));
                appliedCount++;
            }
            catch (Exception)
            {
                failedCount++;
            }
        }

        return (appliedCount, failedCount);
    }

    public MailListDisplayMode MailItemDisplayMode
    {
        get => _configurationService.Get(nameof(MailItemDisplayMode), MailListDisplayMode.Spacious);
        set => SetPropertyAndSave(nameof(MailItemDisplayMode), value);
    }

    public bool IsHardDeleteProtectionEnabled
    {
        get => _configurationService.Get(nameof(IsHardDeleteProtectionEnabled), true);
        set => SetPropertyAndSave(nameof(IsHardDeleteProtectionEnabled), value);
    }

    public bool IsThreadingEnabled
    {
        get => _configurationService.Get(nameof(IsThreadingEnabled), true);
        set => SetPropertyAndSave(nameof(IsThreadingEnabled), value);
    }

    public bool IsNewestThreadMailFirst
    {
        get => _configurationService.Get(nameof(IsNewestThreadMailFirst), true);
        set => SetPropertyAndSave(nameof(IsNewestThreadMailFirst), value);
    }

    public bool IsMailListActionBarEnabled
    {
        get => _configurationService.Get(nameof(IsMailListActionBarEnabled), false);
        set => SetPropertyAndSave(nameof(IsMailListActionBarEnabled), value);
    }

    public bool IsShowActionLabelsEnabled
    {
        get => _configurationService.Get(nameof(IsShowActionLabelsEnabled), true);
        set => SetPropertyAndSave(nameof(IsShowActionLabelsEnabled), value);
    }

    public bool IsShowSenderPicturesEnabled
    {
        get => _configurationService.Get(nameof(IsShowSenderPicturesEnabled), true);
        set => SetPropertyAndSave(nameof(IsShowSenderPicturesEnabled), value);
    }

    public bool IsShowPreviewEnabled
    {
        get => _configurationService.Get(nameof(IsShowPreviewEnabled), true);
        set => SetPropertyAndSave(nameof(IsShowPreviewEnabled), value);
    }

    public bool RenderStyles
    {
        get => _configurationService.Get(nameof(RenderStyles), true);
        set => SetPropertyAndSave(nameof(RenderStyles), value);
    }

    public bool RenderPlaintextLinks
    {
        get => _configurationService.Get(nameof(RenderPlaintextLinks), true);
        set => SetPropertyAndSave(nameof(RenderPlaintextLinks), value);
    }

    public bool RenderImages
    {
        get => _configurationService.Get(nameof(RenderImages), true);
        set => SetPropertyAndSave(nameof(RenderImages), value);
    }

    public bool Prefer24HourTimeFormat
    {
        get => _configurationService.Get(nameof(Prefer24HourTimeFormat), false);
        set => SetPropertyAndSave(nameof(Prefer24HourTimeFormat), value);
    }

    public MailMarkAsOption MarkAsPreference
    {
        get => _configurationService.Get(nameof(MarkAsPreference), MailMarkAsOption.WhenSelected);
        set => SetPropertyAndSave(nameof(MarkAsPreference), value);
    }

    public int MarkAsDelay
    {
        get => _configurationService.Get(nameof(MarkAsDelay), 5);
        set => SetPropertyAndSave(nameof(MarkAsDelay), value);
    }

    public MailOperation RightSwipeOperation
    {
        get => _configurationService.Get(nameof(RightSwipeOperation), MailOperation.MarkAsRead);
        set => SetPropertyAndSave(nameof(RightSwipeOperation), value);
    }

    public MailOperation LeftSwipeOperation
    {
        get => _configurationService.Get(nameof(LeftSwipeOperation), MailOperation.SoftDelete);
        set => SetPropertyAndSave(nameof(LeftSwipeOperation), value);
    }

    public bool IsHoverActionsEnabled
    {
        get => _configurationService.Get(nameof(IsHoverActionsEnabled), true);
        set => SetPropertyAndSave(nameof(IsHoverActionsEnabled), value);
    }

    public MailOperation LeftHoverAction
    {
        get => _configurationService.Get(nameof(LeftHoverAction), MailOperation.Archive);
        set => SetPropertyAndSave(nameof(LeftHoverAction), value);
    }

    public MailOperation CenterHoverAction
    {
        get => _configurationService.Get(nameof(CenterHoverAction), MailOperation.SoftDelete);
        set => SetPropertyAndSave(nameof(CenterHoverAction), value);
    }

    public MailOperation RightHoverAction
    {
        get => _configurationService.Get(nameof(RightHoverAction), MailOperation.SetFlag);
        set => SetPropertyAndSave(nameof(RightHoverAction), value);
    }

    public bool IsLoggingEnabled
    {
        get => _configurationService.Get(nameof(IsLoggingEnabled), true);
        set => SetPropertyAndSave(nameof(IsLoggingEnabled), value);
    }

    public bool IsGravatarEnabled
    {
        get => _configurationService.Get(nameof(IsGravatarEnabled), true);
        set => SetPropertyAndSave(nameof(IsGravatarEnabled), value);
    }

    public bool IsFaviconEnabled
    {
        get => _configurationService.Get(nameof(IsFaviconEnabled), true);
        set => SetPropertyAndSave(nameof(IsFaviconEnabled), value);
    }

    public Guid? StartupEntityId
    {
        get => _configurationService.Get<Guid?>(nameof(StartupEntityId), null);
        set => SaveProperty(propertyName: nameof(StartupEntityId), value);
    }

    public MailOperation FirstMailNotificationAction
    {
        get => _configurationService.Get(nameof(FirstMailNotificationAction), MailOperation.MarkAsRead);
        set => SetPropertyAndSave(nameof(FirstMailNotificationAction), value);
    }

    public MailOperation SecondMailNotificationAction
    {
        get => _configurationService.Get(nameof(SecondMailNotificationAction), MailOperation.SoftDelete);
        set => SetPropertyAndSave(nameof(SecondMailNotificationAction), value);
    }

    public AppLanguage CurrentLanguage
    {
        get => _configurationService.Get(nameof(CurrentLanguage), TranslationService.DefaultAppLanguage);
        set => SaveProperty(propertyName: nameof(CurrentLanguage), value);
    }

    public string ReaderFont
    {
        get => _configurationService.Get(nameof(ReaderFont), "Calibri");
        set => SaveProperty(propertyName: nameof(ReaderFont), value);
    }

    public int ReaderFontSize
    {
        get => _configurationService.Get(nameof(ReaderFontSize), 14);
        set => SaveProperty(propertyName: nameof(ReaderFontSize), value);
    }

    public string ComposerFont
    {
        get => _configurationService.Get(nameof(ComposerFont), "Calibri");
        set => SaveProperty(propertyName: nameof(ComposerFont), value);
    }

    public int ComposerFontSize
    {
        get => _configurationService.Get(nameof(ComposerFontSize), 14);
        set => SaveProperty(propertyName: nameof(ComposerFontSize), value);
    }

    public bool IsNavigationPaneOpened
    {
        get => _configurationService.Get(nameof(IsNavigationPaneOpened), true);
        set => SetPropertyAndSave(propertyName: nameof(IsNavigationPaneOpened), value);
    }

    public bool AutoSelectNextItem
    {
        get => _configurationService.Get(nameof(AutoSelectNextItem), true);
        set => SaveProperty(propertyName: nameof(AutoSelectNextItem), value);
    }

    public string DiagnosticId
    {
        get => _configurationService.Get(nameof(DiagnosticId), Guid.NewGuid().ToString());
        set => SaveProperty(propertyName: nameof(DiagnosticId), value);
    }

    public SearchMode DefaultSearchMode
    {
        get => _configurationService.Get(nameof(DefaultSearchMode), SearchMode.Local);
        set => SaveProperty(propertyName: nameof(DefaultSearchMode), value);
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _configurationService.Get(nameof(FirstDayOfWeek), DayOfWeek.Monday);
        set => SaveProperty(propertyName: nameof(FirstDayOfWeek), value);
    }

    public double HourHeight
    {
        get => _configurationService.Get(nameof(HourHeight), 60.0);
        set => SaveProperty(propertyName: nameof(HourHeight), value);
    }

    public bool IsWorkingHoursEnabled
    {
        get => _configurationService.Get(nameof(IsWorkingHoursEnabled), true);
        set => SaveProperty(propertyName: nameof(IsWorkingHoursEnabled), value);
    }

    public string CalendarTimedDayHeaderDateFormat
    {
        get => _configurationService.Get(nameof(CalendarTimedDayHeaderDateFormat), "ddd dd");
        set => SaveProperty(propertyName: nameof(CalendarTimedDayHeaderDateFormat), string.IsNullOrWhiteSpace(value) ? "ddd dd" : value.Trim());
    }

    public TimeSpan WorkingHourStart
    {
        get => _configurationService.Get(nameof(WorkingHourStart), new TimeSpan(9, 0, 0));
        set => SaveProperty(propertyName: nameof(WorkingHourStart), value);
    }

    public TimeSpan WorkingHourEnd
    {
        get => _configurationService.Get(nameof(WorkingHourEnd), new TimeSpan(18, 0, 0));
        set => SaveProperty(propertyName: nameof(WorkingHourEnd), value);
    }

    public DayOfWeek WorkingDayStart
    {
        get => _configurationService.Get(nameof(WorkingDayStart), DayOfWeek.Monday);
        set => SaveProperty(propertyName: nameof(WorkingDayStart), value);
    }

    public DayOfWeek WorkingDayEnd
    {
        get => _configurationService.Get(nameof(WorkingDayEnd), DayOfWeek.Friday);
        set => SaveProperty(propertyName: nameof(WorkingDayEnd), value);
    }

    public long DefaultReminderDurationInSeconds
    {
        get => _configurationService.Get(nameof(DefaultReminderDurationInSeconds), 900L); // Default: 15 minutes (900 seconds)
        set => SaveProperty(propertyName: nameof(DefaultReminderDurationInSeconds), value);
    }

    public int DefaultSnoozeDurationInMinutes
    {
        get => _configurationService.Get(nameof(DefaultSnoozeDurationInMinutes), 5);
        set => SaveProperty(propertyName: nameof(DefaultSnoozeDurationInMinutes), value);
    }

    public NewEventButtonBehavior NewEventButtonBehavior
    {
        get => _configurationService.Get(nameof(NewEventButtonBehavior), NewEventButtonBehavior.AskEachTime);
        set => SetPropertyAndSave(nameof(NewEventButtonBehavior), value);
    }

    public Guid? DefaultNewEventCalendarId
    {
        get => _configurationService.Get<Guid?>(nameof(DefaultNewEventCalendarId), null);
        set => SetPropertyAndSave(nameof(DefaultNewEventCalendarId), value);
    }

    public int EmailSyncIntervalMinutes
    {
        get => _configurationService.Get(nameof(EmailSyncIntervalMinutes), 3);
        set => SetPropertyAndSave(nameof(EmailSyncIntervalMinutes), value);
    }

    public bool IsStoreUpdateNotificationsEnabled
    {
        get => _configurationService.Get(nameof(IsStoreUpdateNotificationsEnabled), true);
        set => SetPropertyAndSave(nameof(IsStoreUpdateNotificationsEnabled), value);
    }

    public bool IsSystemTrayIconEnabled
    {
        get => _configurationService.Get(nameof(IsSystemTrayIconEnabled), true);
        set => SetPropertyAndSave(nameof(IsSystemTrayIconEnabled), value);
    }

    public bool IsWinoAccountButtonHidden
    {
        get => _configurationService.Get(nameof(IsWinoAccountButtonHidden), false);
        set => SetPropertyAndSave(nameof(IsWinoAccountButtonHidden), value);
    }

    public bool IsAiActionsPanelHidden
    {
        get => _configurationService.Get(nameof(IsAiActionsPanelHidden), false);
        set => SetPropertyAndSave(nameof(IsAiActionsPanelHidden), value);
    }

    public string AiDefaultTranslationLanguageCode
    {
        get => _configurationService.Get(nameof(AiDefaultTranslationLanguageCode), "en-US");
        set => SetPropertyAndSave(nameof(AiDefaultTranslationLanguageCode), string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim());
    }

    public string AiSummarizeLanguageCode
    {
        get => _configurationService.Get(nameof(AiSummarizeLanguageCode), "en-US");
        set => SetPropertyAndSave(nameof(AiSummarizeLanguageCode), string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim());
    }

    public string AiSummarySavePath
    {
        get => _configurationService.Get(nameof(AiSummarySavePath), string.Empty);
        set => SetPropertyAndSave(nameof(AiSummarySavePath), value?.Trim() ?? string.Empty);
    }

    public WinoApplicationMode DefaultApplicationMode
    {
        get
        {
            var configuredMode = _configurationService.Get(nameof(DefaultApplicationMode), WinoApplicationMode.Mail);

            return Enum.IsDefined(typeof(WinoApplicationMode), configuredMode)
                ? configuredMode
                : WinoApplicationMode.Mail;
        }
        set => SaveProperty(propertyName: nameof(DefaultApplicationMode), value);
    }

    public CalendarSettings GetCurrentCalendarSettings()
    {
        var workingDays = GetDaysBetween(WorkingDayStart, WorkingDayEnd);

        return new CalendarSettings(FirstDayOfWeek,
                                    workingDays,
                                    IsWorkingHoursEnabled,
                                    WorkingDayStart,
                                    WorkingDayEnd,
                                    WorkingHourStart,
                                    WorkingHourEnd,
                                    HourHeight,
                                    Prefer24HourTimeFormat ? DayHeaderDisplayType.TwentyFourHour : DayHeaderDisplayType.TwelveHour,
                                    new CultureInfo(WinoTranslationDictionary.GetLanguageFileNameRelativePath(CurrentLanguage)),
                                    CalendarTimedDayHeaderDateFormat);
    }

    private List<DayOfWeek> GetDaysBetween(DayOfWeek startDay, DayOfWeek endDay)
    {
        var daysOfWeek = new List<DayOfWeek>();

        int currentDay = (int)startDay;
        int endDayInt = (int)endDay;

        // If endDay is before startDay in the week, wrap around
        if (endDayInt < currentDay)
        {
            endDayInt += 7;
        }

        // Collect days from startDay to endDay
        while (currentDay <= endDayInt)
        {
            daysOfWeek.Add((DayOfWeek)(currentDay % 7));
            currentDay++;
        }

        return daysOfWeek;
    }

    private static void WritePreferenceValue(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        switch (value)
        {
            case string stringValue:
                writer.WriteString(propertyName, stringValue);
                return;
            case bool boolValue:
                writer.WriteBoolean(propertyName, boolValue);
                return;
            case int intValue:
                writer.WriteNumber(propertyName, intValue);
                return;
            case long longValue:
                writer.WriteNumber(propertyName, longValue);
                return;
            case double doubleValue:
                writer.WriteNumber(propertyName, doubleValue);
                return;
            case float floatValue:
                writer.WriteNumber(propertyName, floatValue);
                return;
            case Guid guidValue:
                writer.WriteString(propertyName, guidValue);
                return;
            case TimeSpan timeSpanValue:
                writer.WriteString(propertyName, timeSpanValue.ToString("c", CultureInfo.InvariantCulture));
                return;
        }

        var valueType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (valueType.IsEnum)
        {
            writer.WriteString(propertyName, value.ToString());
            return;
        }

        writer.WriteString(propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static object? ReadPreferenceValue(Type propertyType, JsonElement value)
    {
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (targetType == typeof(string))
        {
            return value.GetString() ?? string.Empty;
        }

        if (targetType == typeof(bool))
        {
            return value.GetBoolean();
        }

        if (targetType == typeof(int))
        {
            return value.GetInt32();
        }

        if (targetType == typeof(long))
        {
            return value.GetInt64();
        }

        if (targetType == typeof(double))
        {
            return value.GetDouble();
        }

        if (targetType == typeof(float))
        {
            return value.GetSingle();
        }

        if (targetType == typeof(Guid))
        {
            return Guid.Parse(value.GetString() ?? string.Empty);
        }

        if (targetType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(value.GetString() ?? string.Empty, CultureInfo.InvariantCulture);
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value.GetString() ?? string.Empty, true);
        }

        return Convert.ChangeType(value.GetString(), targetType, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<PropertyInfo> GetSyncablePreferenceProperties()
    {
        foreach (var property in typeof(IPreferencesService).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            yield return property;
        }
    }
}


