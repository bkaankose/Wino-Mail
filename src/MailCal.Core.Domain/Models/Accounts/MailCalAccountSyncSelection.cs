namespace MailCal.Core.Domain.Models.Accounts;

public sealed record MailCalAccountSyncSelection(
    bool IncludePreferences = true,
    bool IncludeAccounts = true);
