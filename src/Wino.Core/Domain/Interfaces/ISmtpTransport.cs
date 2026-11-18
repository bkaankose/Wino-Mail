using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface ISmtpTransport
{
    Task<MimeMessage> SendAsync(
        MailAccount account,
        MimeMessage draftMessage,
        CancellationToken cancellationToken = default);
}
