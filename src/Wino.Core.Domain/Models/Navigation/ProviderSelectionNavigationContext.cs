#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Models.Navigation;

public enum ProviderSelectionHostMode
{
    Wizard,
    SettingsAddAccount
}

public sealed class ProviderSelectionNavigationContext
{
    public ProviderSelectionHostMode HostMode { get; init; } = ProviderSelectionHostMode.Wizard;

    /// <summary>
    /// Accounts picked from other mail clients. Account setup runs once for each, starting at <see cref="ImportIndex"/>,
    /// so every imported account goes through the same steps as one the user adds by hand.
    /// </summary>
    public IReadOnlyList<MailClientImportCandidate>? ImportedAccounts { get; init; }

    public int ImportIndex { get; init; }

    /// <summary>The last account an earlier step of the import added; the shell shows it when the import ends.</summary>
    public MailAccount? LastImportedAccount { get; init; }

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

    public static ProviderSelectionNavigationContext CreateForImport(ProviderSelectionHostMode hostMode,
                                                                     IReadOnlyList<MailClientImportCandidate> accounts,
                                                                     int index = 0,
                                                                     MailAccount? lastImportedAccount = null)
        => new()
        {
            HostMode = hostMode,
            ImportedAccounts = accounts,
            ImportIndex = index,
            LastImportedAccount = lastImportedAccount
        };

    public bool IsWizardHost => HostMode == ProviderSelectionHostMode.Wizard;
}
