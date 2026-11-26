#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services.Interfaces;

/// <summary>The registered mail provider modules, looked up by provider type.</summary>
public interface IMailProviderRegistry
{
    IReadOnlyList<IMailProviderModule> Modules { get; }

    IMailProviderModule? Find(MailProviderType providerType);

    /// <exception cref="System.NotSupportedException">No module is registered for the provider type.</exception>
    IMailProviderModule Get(MailProviderType providerType);

    /// <summary>The module's traits, or <see cref="MailProviderTraits.None"/> for an unregistered provider type.</summary>
    MailProviderTraits GetTraits(MailProviderType providerType);

    /// <summary>
    /// The traits for this account, which can differ from the provider's by the account's connection method (an Exchange
    /// account over ActiveSync, for example). <see cref="MailProviderTraits.None"/> for an unregistered provider type.
    /// </summary>
    MailProviderTraits GetTraits(MailAccount account);
}
