#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Assistant;

/// <summary>The device's clock and the user's time preferences for the runtime: the calendar's week start and working hours, the UI culture and the languages to expect.</summary>
public sealed class AgentClock(IPreferencesService preferences) : IAgentClock
{
    public DateTime LocalNow => DateTime.Now;

    public string TimeZoneId => TimeZoneInfo.Local.Id;

    public DayOfWeek WeekStart => preferences.FirstDayOfWeek;

    public TimeOnly? WorkDayStart => HasWorkingHours ? TimeOnly.FromTimeSpan(preferences.WorkingHourStart) : null;

    public TimeOnly? WorkDayEnd => HasWorkingHours ? TimeOnly.FromTimeSpan(preferences.WorkingHourEnd) : null;

    public string? UiCulture => CultureInfo.CurrentUICulture.Name;

    public IReadOnlyList<string> ExpectedLanguages
    {
        get
        {
            var languages = new List<string>();
            var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (ui.Length == 2) languages.Add(ui);
            if (!languages.Contains("en")) languages.Add("en");
            return languages;
        }
    }

    private bool HasWorkingHours => preferences.IsWorkingHoursEnabled && preferences.WorkingHourStart >= TimeSpan.Zero
        && preferences.WorkingHourEnd > preferences.WorkingHourStart && preferences.WorkingHourEnd < TimeSpan.FromDays(1);
}
