#nullable enable

using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Navigation;

public sealed record SettingsPageActivationContext(WinoPage TargetPage, object? PageParameter = null);
