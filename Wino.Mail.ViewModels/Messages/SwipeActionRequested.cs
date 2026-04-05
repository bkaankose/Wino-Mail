using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// When a swipe action is performed on a mail item container.
/// </summary>
public record SwipeActionRequested(MailOperation Operation, IMailListItem MailItem);