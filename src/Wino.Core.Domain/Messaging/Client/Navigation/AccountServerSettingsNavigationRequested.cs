using System;

namespace Wino.Messaging.Client.Navigation;

/// <summary>
/// Opens Settings > Accounts > the account > server settings, for example from a calendar or contacts certificate notice.
/// </summary>
public sealed record AccountServerSettingsNavigationRequested(Guid AccountId);
