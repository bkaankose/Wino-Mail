using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IOutlookAuthenticator : IAuthenticator
{
    Task<Models.Authentication.TokenInformationEx> GenerateTokenInformationAsync(
        Entities.Shared.MailAccount account,
        nint parentWindowHandle,
        CancellationToken cancellationToken);
}

public interface IGmailAuthenticator : IAuthenticator
{
    bool ProposeCopyAuthURL { get; set; }
}

public interface IImapAuthenticator : IAuthenticator { }
