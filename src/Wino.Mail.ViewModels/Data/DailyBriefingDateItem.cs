#nullable enable
using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Mail.ViewModels;

/// <summary>
/// One received-day group in the briefing. Cards are grouped by the day the message
/// arrived, which is the only date the briefing states.
/// </summary>
public sealed partial class DailyBriefingDateItem : ObservableObject
{
    public DateOnly Date { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string SecondaryName { get; init; } = string.Empty;

    public ObservableCollection<DailyBriefingItem> Items { get; init; } = [];
}
