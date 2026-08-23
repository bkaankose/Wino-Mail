#nullable enable
using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

public partial class WinoIntelligenceMailboxItemViewModel : ObservableObject
{
    public required Guid MailboxId { get; init; }
    public required string Address { get; init; }
    public required string IntelligenceSummary { get; init; }
    public required MailProviderType ProviderType { get; init; }
    public SpecialImapProvider SpecialProvider { get; init; }
    public Guid? LocalAccountId { get; init; }
    public MailAccount? Account { get; init; }
    public bool HasServerIntelligence { get; init; }
    public bool CanToggle { get; init; }

    public string ProviderImage => $"ms-appx:///Assets/Providers/{(SpecialProvider == SpecialImapProvider.None ? ProviderType : SpecialProvider)}.png";
    public bool CanManage => LocalAccountId.HasValue;
    public bool IsManageUnavailable => !CanManage;

    /// <summary>
    /// Explains why Manage is disabled. Null for mailboxes that are present on this
    /// device, so they get no tooltip at all.
    /// </summary>
    public string? ManageUnavailableTooltip => IsManageUnavailable
        ? Translator.WinoAccount_Management_IntelligenceManageUnavailable
        : null;
    public string ManageAutomationId => $"WinoAccountIntelligenceManage_{MailboxId:N}";
    public string DeleteAutomationId => $"WinoAccountIntelligenceDelete_{MailboxId:N}";
    public string ToggleAutomationId => $"WinoAccountIntelligenceToggle_{MailboxId:N}";
    public string? ToggleUnavailableTooltip => CanToggle
        ? null
        : Translator.WinoIntelligence_MailboxToggleUnavailable;
    public ICommand? ManageCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
    public ICommand? ToggleEnabledCommand { get; init; }

    [ObservableProperty]
    public partial bool IsDeleting { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsChangingEnabled { get; set; }
}
