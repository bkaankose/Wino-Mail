using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// <see cref="IMailMovePresenter"/> on iPhone: the reader's Move opens the folder list's Move sheet (medium and large
/// detents) for the open message. The iPad twin is <c>PadMailMovePresenter</c>, a popover anchored to the button.
/// False when no folder list is the shell's content or the mails span accounts; the reader then falls back to the
/// shared folder picker.
/// </summary>
public sealed class PhoneMailMovePresenter(IServiceProvider services) : IMailMovePresenter
{
    public bool TryShowMove(IReadOnlyList<MailCopy> mails, UIView anchor)
        => services.GetService<UIKitNavigationService>()?.Shell is PhoneShellViewController shell && shell.TryShowMove(mails);
}
