#nullable enable

using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Navigation;

public sealed record SettingsNavigationRouteStep(string PageTitle, MailCalPage PageType, object? Parameter = null);

public sealed record SettingsNavigationRoute(IReadOnlyList<SettingsNavigationRouteStep> Steps)
{
    public SettingsNavigationRouteStep Destination
        => Steps.Count > 0
            ? Steps[^1]
            : throw new InvalidOperationException("A settings navigation route must contain at least one step.");
}

public enum AccountDetailsTab
{
    General,
    Mail,
    Calendar,
    People,
    ToDo
}

/// <param name="OpenServerSettings">Opens the IMAP/CalDAV server settings on top of the General tab once the page loads.</param>
public sealed record AccountDetailsNavigationContext(Guid AccountId, AccountDetailsTab SelectedTab, bool OpenServerSettings = false);
