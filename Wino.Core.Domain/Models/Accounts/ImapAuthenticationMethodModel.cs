using Wino.Domain.Enums;

namespace Wino.Domain.Models.Accounts
{
    public class ImapAuthenticationMethodModel(ImapAuthenticationMethod imapAuthenticationMethod, string displayName)
    {
        public ImapAuthenticationMethod ImapAuthenticationMethod { get; } = imapAuthenticationMethod;
        public string DisplayName { get; } = displayName;
    }
}
