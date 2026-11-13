#nullable enable
using System.Threading;
using MimeKit;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class MailRenderingPageViewModel
{
    public MailInvitationCardViewModel Invitation { get; }

    private void ResetInvitation() => Invitation.Reset();

    /// <summary>Runs beside the body render; the card appears once the service has read the invitation.</summary>
    private void BeginInvitationLoad(MailItemViewModel mailItem, MimeMessage message, CancellationToken cancellationToken)
    {
        if (mailItem.MailCopy == null)
            return;

        _ = Invitation.LoadAsync(mailItem.MailCopy, message, cancellationToken, mailItem.NotifyCalendarDetailsAvailable);
    }
}
