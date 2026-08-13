namespace Wino.Core.Domain.Models.Accounts;

public sealed record WinoAccountSyncSelection(
    bool IncludePreferences = true,
    bool IncludeAccounts = true);
