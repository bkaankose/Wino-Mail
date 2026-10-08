using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Builds the "Mail + Calendar + People + To Do" line shown under an account.
/// A mode counts when it is on for the account, whether it runs against the provider or locally.
/// </summary>
public static class AccountCapabilitySummary
{
    private const string Separator = " + ";

    public static string Build(MailAccount account)
    {
        if (account is null)
            return Translator.AccountCapability_None;

        var parts = new List<string>(4);

        if (account.IsMailAccessGranted)
            parts.Add(Translator.AccountCapability_Mail);

        if (account.IsCalendarAccessEnabled || account.IsCalendarAccessGranted)
            parts.Add(Translator.AccountCapability_Calendar);

        if (account.IsContactAccessEnabled || account.IsContactAccessGranted)
            parts.Add(Translator.AccountCapability_People);

        if (account.IsTaskAccessEnabled || account.IsTaskAccessGranted)
            parts.Add(Translator.AccountCapability_ToDo);

        return parts.Count == 0
            ? Translator.AccountCapability_None
            : string.Join(Separator, parts);
    }
}
