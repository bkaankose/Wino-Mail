#nullable enable

using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Navigation;

public sealed record SettingsNavigationRouteStep(string PageTitle, WinoPage PageType, object? Parameter = null);

public sealed record SettingsNavigationRoute(IReadOnlyList<SettingsNavigationRouteStep> Steps)
{
    public SettingsNavigationRouteStep Destination
        => Steps.Count > 0
            ? Steps[^1]
            : throw new InvalidOperationException("A settings navigation route must contain at least one step.");

    /// <summary>
    /// Route to a page that hangs off one account's details page: manage accounts, then the account
    /// on <paramref name="accountTab"/>, then the page itself with the account id as its parameter.
    /// </summary>
    public static SettingsNavigationRoute ForAccountSubpage(MailAccount account, string pageTitle, WinoPage pageType, AccountDetailsTab accountTab)
    {
        ArgumentNullException.ThrowIfNull(account);

        var accountTitle = !string.IsNullOrWhiteSpace(account.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account.Name ?? Translator.AccountDetailsPage_Title;

        return new SettingsNavigationRoute(new[]
        {
            new SettingsNavigationRouteStep(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage),
            new SettingsNavigationRouteStep(accountTitle, WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(account.Id, accountTab)),
            new SettingsNavigationRouteStep(pageTitle, pageType, account.Id)
        });
    }
}

public enum AccountDetailsTab
{
    General,
    Mail,
    Calendar,
    People,
    ToDo
}

public sealed record AccountDetailsNavigationContext(Guid AccountId, AccountDetailsTab SelectedTab);
