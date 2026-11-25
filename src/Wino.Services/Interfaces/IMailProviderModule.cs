#nullable enable
using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Interfaces;

/// <summary>
/// One mail provider: its data, its authenticator and its synchronizer. Shared code asks the module through
/// <see cref="IMailProviderRegistry"/> instead of switching on the provider type.
/// </summary>
public interface IMailProviderModule
{
    MailProviderType ProviderType { get; }

    MailProviderTraits Traits { get; }

    /// <summary>The traits for one account; a provider whose transports differ answers per account, the others with <see cref="Traits"/>.</summary>
    MailProviderTraits GetTraits(MailAccount account) => Traits;

    /// <summary>A new authenticator, or null when the provider signs in only with credentials stored on the account.</summary>
    IAuthenticator? CreateAuthenticator();

    IWinoSynchronizerBase CreateSynchronizer(MailAccount account);

    /// <summary>
    /// Whether a synchronizer built earlier still fits the account. Called only after the shared settings check found no
    /// change, for state the provider keeps on its synchronizer.
    /// </summary>
    bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account);

    /// <summary>Whether an error from the provider's API means the granted permission was revoked or expired.</summary>
    bool IsAuthorizationFailure(Exception exception);
}
