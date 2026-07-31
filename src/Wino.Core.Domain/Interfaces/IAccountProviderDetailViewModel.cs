using System;

namespace Wino.Core.Domain.Interfaces;

public interface IAccountProviderDetailViewModel
{
    /// <summary>
    /// Entity id that will help to identify the startup entity on launch.
    /// </summary>
    Guid StartupEntityId { get; }

    /// <summary>
    /// Name representation of the view model that will be used to identify the startup entity on launch.
    /// </summary>
    string StartupEntityTitle { get; }

    /// <summary>
    /// E-mail addresses that this account holds.
    /// </summary>

    string StartupEntityAddresses { get; }

    /// <summary>
    /// Represents the account order in the accounts list.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Provider details of the account.
    /// </summary>
    IProviderDetail ProviderDetail { get; set; }

    /// <summary>
    /// How many accounts this provider has.
    /// </summary>
    int HoldingAccountCount { get; }
}
