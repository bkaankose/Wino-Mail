namespace Wino.Core.Domain.Models.Navigation;

public enum ProviderSelectionHostMode
{
    Wizard,
    SettingsAddAccount
}

public sealed class ProviderSelectionNavigationContext
{
    public ProviderSelectionHostMode HostMode { get; init; } = ProviderSelectionHostMode.Wizard;

    public static ProviderSelectionNavigationContext CreateForWizard()
        => new()
        {
            HostMode = ProviderSelectionHostMode.Wizard
        };

    public static ProviderSelectionNavigationContext CreateForSettingsAddAccount()
        => new()
        {
            HostMode = ProviderSelectionHostMode.SettingsAddAccount
        };

    public bool IsWizardHost => HostMode == ProviderSelectionHostMode.Wizard;
}
