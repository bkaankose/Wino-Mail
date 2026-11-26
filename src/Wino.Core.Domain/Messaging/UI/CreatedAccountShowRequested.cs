using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>
/// Shows an account whose <see cref="AccountCreatedMessage"/> did not, once every account of an import is set up: the
/// shell switches to the first mode it provides, as it does for a single new account.
/// </summary>
public record CreatedAccountShowRequested(MailAccount Account) : UIMessageBase<CreatedAccountShowRequested>;
