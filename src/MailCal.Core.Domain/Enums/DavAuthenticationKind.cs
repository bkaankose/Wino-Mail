namespace MailCal.Core.Domain.Enums;

public enum DavAuthenticationKind
{
    Basic = 0,
    Digest = 1,
    Bearer = 2,

    /// <summary>No Authorization header, for downloads from an origin that must not receive the account credentials.</summary>
    Anonymous = 3
}
