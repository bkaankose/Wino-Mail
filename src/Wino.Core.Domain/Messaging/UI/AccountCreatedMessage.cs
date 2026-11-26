using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

/// <param name="ShowsAccount">
/// False while more accounts are being set up after this one (an import): the shell stays where it is until
/// <see cref="CreatedAccountShowRequested"/> arrives for the last account.
/// </param>
public record AccountCreatedMessage(MailAccount Account, bool ShowsAccount = true) : UIMessageBase<AccountCreatedMessage>;
