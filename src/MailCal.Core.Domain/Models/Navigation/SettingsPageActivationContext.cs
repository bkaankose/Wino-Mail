#nullable enable

using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Navigation;

public sealed record SettingsPageActivationContext(MailCalPage TargetPage, object? PageParameter = null);
