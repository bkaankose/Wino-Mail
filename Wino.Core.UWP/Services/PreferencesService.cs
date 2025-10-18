using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Translations;
using Wino.Services;

namespace Wino.Core.UWP.Services;

public class PreferencesService(IConfigurationService configurationService) : ObservableObject, IPreferencesService
{
    private readonly IConfigurationService _configurationService = configurationService;

    public event EventHandler<string> PreferenceChanged;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        PreferenceChanged?.Invoke(this, e.PropertyName);
    }

    private void SaveProperty(string propertyName, object value) => _configurationService.Set(propertyName, value);

    private void SetPropertyAndSave(string propertyName, object value)
    {
        _configurationService.Set(propertyName, value);

        OnPropertyChanged(propertyName);
        Debug.WriteLine($"PreferencesService -> {propertyName}:{value?.ToString()}");
    }

    public MailRenderingOptions GetRenderingOptions()
        => new MailRenderingOptions()
        {
            LoadImages = RenderImages,
            LoadStyles = RenderStyles,
            RenderPlaintextLinks = RenderPlaintextLinks
        };

    public MailListDisplayMode MailItemDisplayMode
    {
        get => _configurationService.Get(nameof(MailItemDisplayMode), MailListDisplayMode.Spacious);
        set => SetPropertyAndSave(nameof(MailItemDisplayMode), value);
    }

    public bool IsSemanticZoomEnabled
    {
        get => _configurationService.Get(nameof(IsSemanticZoomEnabled), true);
        set => SetPropertyAndSave(nameof(IsSemanticZoomEnabled), value);
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

    public bool IsMailkitProtocolLoggerEnabled
    {
        get => _configurationService.Get(nameof(IsMailkitProtocolLoggerEnabled), false);
        set => SetPropertyAndSave(nameof(IsMailkitProtocolLoggerEnabled), value);
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
        set => SaveProperty(propertyName: nameof(IsNavigationPaneOpened), value);
    }

    public bool AutoSelectNextItem
    {
        get => _configurationService.Get(nameof(AutoSelectNextItem), true);
        set => SaveProperty(propertyName: nameof(AutoSelectNextItem), value);
    }

    public ServerBackgroundMode ServerTerminationBehavior
    {
        get => _configurationService.Get(nameof(ServerTerminationBehavior), ServerBackgroundMode.MinimizedTray);
        set => SaveProperty(propertyName: nameof(ServerTerminationBehavior), value);
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

    public TimeSpan WorkingHourStart
    {
        get => _configurationService.Get(nameof(WorkingHourStart), new TimeSpan(8, 0, 0));
        set => SaveProperty(propertyName: nameof(WorkingHourStart), value);
    }

    public TimeSpan WorkingHourEnd
    {
        get => _configurationService.Get(nameof(WorkingHourEnd), new TimeSpan(17, 0, 0));
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

    public int EmailSyncIntervalMinutes
    {
        get => _configurationService.Get(nameof(EmailSyncIntervalMinutes), 3);
        set => SetPropertyAndSave(nameof(EmailSyncIntervalMinutes), value);
    }

    public CalendarSettings GetCurrentCalendarSettings()
    {
        var workingDays = GetDaysBetween(WorkingDayStart, WorkingDayEnd);

        return new CalendarSettings(FirstDayOfWeek,
                                    workingDays,
                                    WorkingHourStart,
                                    WorkingHourEnd,
                                    HourHeight,
                                    Prefer24HourTimeFormat ? DayHeaderDisplayType.TwentyFourHour : DayHeaderDisplayType.TwelveHour,
                                    new CultureInfo(WinoTranslationDictionary.GetLanguageFileNameRelativePath(CurrentLanguage)));
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
}
