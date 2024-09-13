namespace Wino.Core.Domain.Interfaces
{
    public interface IOutlookAuthenticator : IAuthenticator { }
    public interface IGmailAuthenticator : IAuthenticator
    {
        bool ProposeCopyAuthURL { get; set; }
    }
    public interface IImapAuthenticator : IAuthenticator { }
}
