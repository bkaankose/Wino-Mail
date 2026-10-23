using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.CardDav;

public sealed class DavAuthenticationProfile
{
    public DavAuthenticationKind Kind { get; init; } = DavAuthenticationKind.Basic;
    public string Username { get; init; }
    public string Password { get; init; }
    public string BearerToken { get; init; }
}
