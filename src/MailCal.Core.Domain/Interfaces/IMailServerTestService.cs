using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Connectivity;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Answers whether Mail-Cal can connect to and authenticate against a custom mail server.
/// </summary>
public interface IMailServerTestService
{
    /// <summary>
    /// Connects and authenticates against the IMAP and SMTP servers. Throws <see cref="MailCal.Core.Domain.Exceptions.ImapValidationException"/> on failure.
    /// </summary>
    Task TestImapAsync(CustomServerInformation serverInformation);

    /// <summary>
    /// Connects and authenticates against the POP3 server and verifies UIDL support.
    /// </summary>
    Task<Pop3ConnectivityTestResult> TestPop3Async(CustomServerInformation serverInformation, CancellationToken cancellationToken = default);
}
