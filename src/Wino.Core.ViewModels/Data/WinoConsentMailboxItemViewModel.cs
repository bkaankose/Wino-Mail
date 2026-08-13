#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

public partial class WinoConsentMailboxItemViewModel : ObservableObject
{
    public required string Address { get; init; }
    public required MailProviderType ProviderType { get; init; }
    public Guid? LocalAccountId { get; init; }

    [ObservableProperty]
    public partial Guid? MailboxId { get; set; }

    [ObservableProperty]
    public partial bool IsProcessConsentGranted { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string ProviderName => ProviderType switch
    {
        MailProviderType.Outlook => "Outlook",
        MailProviderType.Gmail => "Gmail",
        MailProviderType.IMAP4 => "IMAP",
        _ => ProviderType.ToString(),
    };

    public string AutomationId => $"WinoProcessConsent_{ProviderType}_{Address.Replace('@', '_').Replace('.', '_')}";
}
