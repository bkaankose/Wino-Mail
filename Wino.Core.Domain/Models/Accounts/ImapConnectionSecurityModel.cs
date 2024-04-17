using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts
{
    public class ImapConnectionSecurityModel(ImapConnectionSecurity imapConnectionSecurity, string displayName)
    {
        public ImapConnectionSecurity ImapConnectionSecurity { get; } = imapConnectionSecurity;
        public string DisplayName { get; } = displayName;
    }
}
