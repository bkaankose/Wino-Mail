#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels;

public sealed partial class DailyBriefingDateItem : ObservableObject
{
    public DateOnly Date { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string SecondaryName { get; init; } = string.Empty;
    public ObservableCollection<DailyBriefingAccountGroup> Groups { get; } = [];
    public List<DailyBriefingFact> Facts { get; } = [];
    public bool HasIgnoredFacts { get; set; }
}
