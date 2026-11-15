#nullable enable
namespace Wino.Mail.ViewModels.Data;

/// <summary>What the page that opens Exchange setup already knows. The modes and the account name travel in the wizard context.</summary>
public sealed class ExchangeSettingsNavigationContext
{
    public string EmailAddress { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The EWS URL Autodiscover found, when it found one.</summary>
    public string? EwsUrl { get; init; }

    public string? UserName { get; init; }
}
