namespace Wino.Core.Domain.Models.AutoDiscovery;

public class AutoDiscoveryMinimalSettings
{
    public string DisplayName { get; set; }
    public string Email { get; set; }
    public string Password { get; set; }
    public Enums.CustomIncomingServerType IncomingServerType { get; set; } = Enums.CustomIncomingServerType.IMAP4;
}
