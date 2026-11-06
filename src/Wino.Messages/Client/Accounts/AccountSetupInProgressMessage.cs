using System;

namespace Wino.Messaging.Client.Accounts;

/// <summary>
/// Sent as soon as account setup has saved a new account. Automatic synchronization leaves the account alone until
/// <see cref="UI.AccountCreatedMessage"/> or <see cref="UI.AccountRemovedMessage"/> arrives for it, so a background
/// pass never downloads the mailbox while the setup's own steps wait behind it.
/// </summary>
public record AccountSetupInProgressMessage(Guid AccountId);
