using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Interfaces;

public interface ISmtpTransport
{
    Task<MimeMessage> SendAsync(
        MailAccount account,
        MimeMessage draftMessage,
        CancellationToken cancellationToken = default);
}
