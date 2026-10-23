#nullable enable
using System;
using MailCal.Core.Domain.Entities.Calendar;

namespace MailCal.Mail.ViewModels.Companion;

/// <summary>A calendar the companion's quick-add form can create events in.</summary>
public sealed class CompanionCalendarOption(AccountCalendar calendar, string accountName)
{
    public AccountCalendar Calendar { get; } = calendar ?? throw new ArgumentNullException(nameof(calendar));
    public Guid Id => Calendar.Id;
    public string Name => Calendar.Name ?? string.Empty;
    public string AccountName { get; } = accountName ?? string.Empty;
    public string ColorHex => Calendar.BackgroundColorHex ?? string.Empty;
    public string AutomationName => string.IsNullOrWhiteSpace(AccountName) ? Name : $"{Name}, {AccountName}";
}
