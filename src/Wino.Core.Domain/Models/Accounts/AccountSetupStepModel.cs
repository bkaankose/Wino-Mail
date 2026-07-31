using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public partial class AccountSetupStepModel : ObservableObject
{
    public string Title { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial AccountSetupStepStatus Status { get; set; } = AccountSetupStepStatus.Pending;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    public bool IsPending => Status == AccountSetupStepStatus.Pending;
    public bool IsInProgress => Status == AccountSetupStepStatus.InProgress;
    public bool IsSucceeded => Status == AccountSetupStepStatus.Succeeded;
    public bool IsFailed => Status == AccountSetupStepStatus.Failed;
    public string AutomationName => string.Format(Translator.Accessibility_AccountSetupStep, Title, GetStatusText());

    public override string ToString() => Title;

    private string GetStatusText() => Status switch
    {
        AccountSetupStepStatus.Pending => Translator.Accessibility_SetupStepPending,
        AccountSetupStepStatus.InProgress => Translator.Accessibility_SetupStepInProgress,
        AccountSetupStepStatus.Succeeded => Translator.Accessibility_SetupStepSucceeded,
        AccountSetupStepStatus.Failed => Translator.Accessibility_SetupStepFailed,
        _ => string.Empty
    };
}
